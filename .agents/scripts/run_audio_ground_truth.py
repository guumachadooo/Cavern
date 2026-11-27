#!/usr/bin/env python3
"""Permanent On-Demand Audio Ground-Truth Pairwise Verification Harness (.NET).

Implements `.agents/docs/AUDIO_GROUND_TRUTH_PROTOCOL.md` (GT-01..GT-07):
  GT-01: LGSR WOLA exact impulse latency (512 samples) + LFE bit-exactness + 0 NaN/Clip
  GT-02: Pure Stereo FLAC (44.1 & 48 kHz) x Direct 2.0 & Virtualizer 7.1.4 x Instant Blind & AI Stereo Stems
  GT-03: 60-second 44.1 kHz -> 48 kHz fractional playhead drift guard + steady-state Karaoke lock
  GT-04: Pure Dolby Atmos (7.1.4 discrete FC + beds) x Instant Blind & AI Stereo Stems & Offline Export
  GT-05: Dolby Atmos + Misaligned FLAC Lossless Twin (+187.35 ms, +25 ppm, -1.2 dB) + ACTIVE LGSR -> Karaoke
  GT-06: W46 3-Stage Async CPU<->GPU 0.0 dB bit-exact parity + progressive 44.1->48 kHz streaming
  GT-07: LR4 120 Hz dedicated .1 LFE crossover & SpatialXRayPowermap smoothing

Usage:
  python3 -B .agents/scripts/run_audio_ground_truth.py [--verbose] [--suite GT-05] [--json /tmp/gt.json]
"""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import math
import os
import platform
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CACHE_DIR = Path("/tmp/cavern_audio_gt_cache")
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
        "No dotnet binary found. Run `python3 -B .agents/scripts/check_android_cs.py --ci-parity` first."
    )


def detect_tfm(dotnet: str) -> str:
    try:
        out = subprocess.check_output([dotnet, "--list-runtimes"], text=True, stderr=subprocess.DEVNULL)
        for line in out.splitlines():
            if line.startswith("Microsoft.NETCore.App "):
                major = line.split()[1].split(".")[0]
                if major.isdigit():
                    return f"net{major}.0"
    except Exception:
        pass
    return "net8.0"


HARNESS_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
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

HARNESS_PROGRAM_CS = r"""
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Decoders;
using Cavern.Format.Environment;
using Cavern.Neural;
using Cavern.ProAudio;
using Cavern.Restoration;
using Cavern.Spatial;
using Cavern.Virtualizer;
using Cavern.VocalChain;
using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

string suiteFilter = Environment.GetEnvironmentVariable("GT_SUITE_FILTER") ?? "";
bool verbose = (Environment.GetEnvironmentVariable("GT_VERBOSE") ?? "0") == "1";
string jsonOutPath = Environment.GetEnvironmentVariable("GT_JSON_OUT") ?? "";
string iamfOutDir = Environment.GetEnvironmentVariable("GT_IAMF_OUT_DIR") ?? "";

List<Dictionary<string, object>> results = [];
int passed = 0;
int failed = 0;

bool MatchesSuiteFilter(string id) {
    if (string.IsNullOrEmpty(suiteFilter)) {
        return true;
    }
    if (suiteFilter.Equals("spatial-cortex-rt", StringComparison.OrdinalIgnoreCase)) {
        return id.Equals("spatial-cortex-rt", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("GT-12", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("GT-13", StringComparison.OrdinalIgnoreCase);
    }
    foreach (string part in suiteFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
        if (id.Equals(part, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
    }
    return false;
}

void RunSuite(string id, string title, Func<Dictionary<string, object>> body) {
    if (!MatchesSuiteFilter(id)) {
        return;
    }
    Console.Write($"[{id}] {title} ... ");
    try {
        Dictionary<string, object> metrics = body();
        metrics["id"] = id;
        metrics["title"] = title;
        metrics["status"] = "PASS";
        results.Add(metrics);
        passed++;
        Console.WriteLine("PASS");
        if (verbose) {
            foreach (var kv in metrics) {
                if (kv.Key is "id" or "title" or "status") continue;
                Console.WriteLine($"    {kv.Key}: {kv.Value}");
            }
        }
    } catch (Exception ex) {
        failed++;
        results.Add(new Dictionary<string, object> {
            ["id"] = id,
            ["title"] = title,
            ["status"] = "FAIL",
            ["error"] = ex.Message
        });
        Console.WriteLine($"FAIL -> {ex.Message}");
    }
}

void Assert(bool cond, string msg) {
    if (!cond) throw new Exception(msg);
}

static (int NaNs, int Clips, float MaxAbs) AuditSignalHealth(float[][] channels, int start, int count) {
    int nans = 0, clips = 0;
    float maxAbs = 0f;
    for (int c = 0; c < channels.Length; c++) {
        float[] ch = channels[c];
        int end = Math.Min(ch.Length, start + count);
        for (int i = Math.Max(0, start); i < end; i++) {
            float v = ch[i];
            if (!float.IsFinite(v)) { nans++; continue; }
            float a = MathF.Abs(v);
            if (a > maxAbs) maxAbs = a;
            if (a > 1.5f) clips++;
        }
    }
    return (nans, clips, maxAbs);
}

static double BandRms(float[] x, int start, int count, int sr, double freqHz) {
    // Goertzel / DFT projection energy at exact deterministic tone frequency
    double re = 0.0, im = 0.0;
    double w = 2.0 * Math.PI * freqHz / sr;
    int end = Math.Min(x.Length, start + count);
    int n = Math.Max(1, end - start);
    for (int i = start; i < end; i++) {
        double phase = w * i;
        re += x[i] * Math.Cos(phase);
        im += x[i] * Math.Sin(phase);
    }
    return 2.0 * Math.Sqrt(re * re + im * im) / n;
}

static double GoertzelPower(float[] x, int start, int count, int sr, double freqHz) {
    double amp = BandRms(x, start, count, sr, freqHz);
    return amp * amp;
}

static double MultiToneRms(float[] x, int start, int count, int sr, double[] freqs) {
    double sumSq = 0.0;
    foreach (double f in freqs) {
        double a = BandRms(x, start, count, sr, f);
        sumSq += a * a;
    }
    return Math.Sqrt(sumSq);
}

static double DbRatio(double num, double den) => 20.0 * Math.Log10((num + 1e-12) / (den + 1e-12));

static VirtualChannel[] BuildVirtual714Layout() => [
    new VirtualChannel { y = -30f, x = 0f, isLfe = false },   // 0 FL
    new VirtualChannel { y = 30f, x = 0f, isLfe = false },    // 1 FR
    new VirtualChannel { y = 0f, x = 0f, isLfe = false },     // 2 FC (strict center)
    new VirtualChannel { y = 0f, x = 0f, isLfe = true },      // 3 LFE
    new VirtualChannel { y = -90f, x = 0f, isLfe = false },   // 4 SL
    new VirtualChannel { y = 90f, x = 0f, isLfe = false },    // 5 SR
    new VirtualChannel { y = -135f, x = 0f, isLfe = false },  // 6 RL
    new VirtualChannel { y = 135f, x = 0f, isLfe = false },   // 7 RR
    new VirtualChannel { y = -30f, x = 45f, isLfe = false },  // 8 TFL
    new VirtualChannel { y = 30f, x = 45f, isLfe = false },   // 9 TFR
    new VirtualChannel { y = -135f, x = 45f, isLfe = false }, // 10 TRL
    new VirtualChannel { y = 135f, x = 45f, isLfe = false },  // 11 TRR
];

// Synthesize deterministic multi-stem vocal (mono + stereo wide) & uncorrelated stereo instruments
static void SynthesizeMultiStem(
    int sr,
    int len,
    out float[] vocL,
    out float[] vocR,
    out float[] instL,
    out float[] instR,
    out float[] lfe) {
    vocL = new float[len];
    vocR = new float[len];
    instL = new float[len];
    instR = new float[len];
    lfe = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        double env = 0.72 + 0.28 * Math.Sin(2.0 * Math.PI * 2.5 * t);
        // Mono centered vocal core (440 + 880 + 1320 Hz) + stereo wide double-tracked vocal harmonic (620 Hz +/-)
        double vMono = env * (0.24 * Math.Sin(2.0 * Math.PI * 440.0 * t)
                            + 0.14 * Math.Sin(2.0 * Math.PI * 880.0 * t)
                            + 0.08 * Math.Sin(2.0 * Math.PI * 1320.0 * t));
        double vSide = env * 0.11 * Math.Sin(2.0 * Math.PI * 620.0 * t + 0.4);
        vocL[i] = (float)(vMono + vSide);
        vocR[i] = (float)(vMono - vSide);

        // Uncorrelated wide stereo instruments (185/1180 Hz L, 245/1640 Hz R)
        instL[i] = (float)(0.18 * Math.Sin(2.0 * Math.PI * 185.0 * t) + 0.14 * Math.Sin(2.0 * Math.PI * 1180.0 * t + 0.7));
        instR[i] = (float)(0.18 * Math.Cos(2.0 * Math.PI * 245.0 * t) + 0.14 * Math.Cos(2.0 * Math.PI * 1640.0 * t + 1.1));
        lfe[i] = (float)(0.35 * Math.Sin(2.0 * Math.PI * 55.0 * t));
    }
}

// ---------- GT-01: LGSR WOLA Exact Impulse Latency (512 samples) + LFE Bit-Exact ----------
RunSuite("GT-01", "LGSR_WOLA_ImpulseLatency_And_LFE_BitExact", () => {
    int sr = 48000;
    int len = 4096;
    int impulseIdx = 600;
    using LgsrSpectralFusionEngine lgsr = new(sr, 4) {
        IsEnabled = true,
        FusionStrength = 0.85f,
        DynamicEnvelopeLock = true,
        ClippingGuardEnabled = true
    };
    float[][] bus = [new float[len], new float[len], new float[len], new float[len]];
    float[] refL = new float[len];
    float[] refR = new float[len];
    bus[0][impulseIdx] = 0.8f;
    bus[1][impulseIdx] = 0.8f;
    refL[impulseIdx] = 0.8f;
    refR[impulseIdx] = 0.8f;
    float[] lfeOrig = new float[len];
    for (int i = 0; i < len; i++) {
        lfeOrig[i] = 0.4f * MathF.Sin(2f * MathF.PI * 55f * i / sr);
        bus[3][i] = lfeOrig[i];
    }
    bool[] isLfe = [false, false, false, true];
    float[] pan = [-1f, 1f, 0f, 0f];

    lgsr.ProcessPreHrtfBusInPlace(bus, isLfe, pan, 4, len, refL, refR, syncConfidence: 0.95f);

    int peakIdx = 0;
    float peakVal = 0f;
    for (int i = 0; i < len; i++) {
        float a = MathF.Abs(bus[0][i]);
        if (a > peakVal) { peakVal = a; peakIdx = i; }
    }
    int measuredDelay = peakIdx - impulseIdx;
    float lfeMaxDiff = 0f;
    for (int i = 0; i < len; i++) {
        lfeMaxDiff = MathF.Max(lfeMaxDiff, MathF.Abs(bus[3][i] - lfeOrig[i]));
    }
    var health = AuditSignalHealth(bus, 0, len);

    Assert(measuredDelay == LgsrSpectralFusionEngine.LatencySamples,
        $"LGSR impulse delay mismatch: measured={measuredDelay}, LatencySamples={LgsrSpectralFusionEngine.LatencySamples}");
    Assert(lfeMaxDiff == 0.0f, $"LGSR modified LFE channel! maxDiff={lfeMaxDiff}");
    Assert(health.NaNs == 0 && health.Clips == 0, $"Signal health failed: NaN={health.NaNs}, Clip={health.Clips}");

    return new Dictionary<string, object> {
        ["measuredDelaySamples"] = measuredDelay,
        ["declaredLatencySamples"] = LgsrSpectralFusionEngine.LatencySamples,
        ["lfeMaxDiff"] = lfeMaxDiff,
        ["nanCount"] = health.NaNs
    };
});

// ---------- GT-02: Pure Stereo FLAC (44.1 & 48 kHz) x Direct 2.0 & Virtualizer 7.1.4 x Blind & AI Stereo Stems ----------
RunSuite("GT-02", "PureStereoFLAC_Direct20_vs_Virtualizer714_Blind_And_AiStereoStems", () => {
    double[] monoVocalFreqs = [440.0, 880.0, 1320.0];
    double[] wideVocalFreqs = [440.0, 620.0, 880.0, 1320.0];
    double[] instLFreqs = [185.0, 1180.0];
    double[] instRFreqs = [245.0, 1640.0];

    int sr = 48000;
    int len = sr * 3; // 3 seconds
    int evalStart = sr; // skip 1s warmup
    int evalLen = sr;
    SynthesizeMultiStem(sr, len, out float[] vocL, out float[] vocR, out float[] instL, out float[] instR, out _);

    // 2A. Direct 2.0 Blind vs Virtualizer 7.1.4 Blind (mono centered vocal + stereo wide instruments)
    float[] monoVoc = new float[len];
    for (int i = 0; i < len; i++) monoVoc[i] = 0.5f * (vocL[i] + vocR[i]);

    float[] dirL = new float[len], dirR = new float[len];
    for (int i = 0; i < len; i++) { dirL[i] = monoVoc[i] + instL[i]; dirR[i] = monoVoc[i] + instR[i]; }

    // Direct 2.0 Blind
    float[] dirInter = new float[len * 2];
    for (int i = 0; i < len; i++) { dirInter[i * 2] = dirL[i]; dirInter[i * 2 + 1] = dirR[i]; }
    using (NeuralStemStageProcessor stDirect = new()) {
        stDirect.SetSourceTrackSampleRate(sr);
        stDirect.Configure(sr, SpatialProgramMetadata.CreateDefault());
        stDirect.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f, KaraokePreserveBackingVocals = true });
        for (int p = 0; p < len; p += 1024) {
            int n = Math.Min(1024, len - p);
            stDirect.ProcessStereoInterleavedKaraoke(dirInter, p * 2, n, p);
        }
    }
    float[] dirOutL = new float[len], dirOutR = new float[len];
    for (int i = 0; i < len; i++) { dirOutL[i] = dirInter[i * 2]; dirOutR[i] = dirInter[i * 2 + 1]; }
    double dirBlindVocalAtten = DbRatio(MultiToneRms(dirL, evalStart, evalLen, sr, monoVocalFreqs),
                                        MultiToneRms(dirOutL, evalStart, evalLen, sr, monoVocalFreqs));
    double dirBlindInstDelta = Math.Abs(DbRatio(MultiToneRms(dirOutL, evalStart, evalLen, sr, instLFreqs),
                                                MultiToneRms(dirL, evalStart, evalLen, sr, instLFreqs)));

    // Virtualizer 7.1.4 Blind (stereo FLAC routed onto FL=0, FR=1 of 12ch Pre-HRTF bus; FC=2 is silent!)
    VirtualChannel[] layout714 = BuildVirtual714Layout();
    float[][] virtBus = new float[12][];
    for (int c = 0; c < 12; c++) virtBus[c] = new float[len];
    Array.Copy(dirL, virtBus[0], len);
    Array.Copy(dirR, virtBus[1], len);
    using (NeuralStemStageProcessor stVirt = new()) {
        stVirt.SetSourceTrackSampleRate(sr);
        stVirt.Configure(sr, SpatialProgramMetadata.CreateDefault());
        stVirt.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f, KaraokePreserveBackingVocals = true });
        float[][] blk = new float[12][];
        for (int c = 0; c < 12; c++) blk[c] = new float[1024];
        for (int p = 0; p < len; p += 1024) {
            int n = Math.Min(1024, len - p);
            for (int c = 0; c < 12; c++) Array.Copy(virtBus[c], p, blk[c], 0, n);
            stVirt.ProcessPreHrtfBus(blk, layout714, 12, n, sr);
            for (int c = 0; c < 12; c++) Array.Copy(blk[c], 0, virtBus[c], p, n);
        }
    }
    double virtBlindVocalAtten = DbRatio(MultiToneRms(dirL, evalStart, evalLen, sr, monoVocalFreqs),
                                         MultiToneRms(virtBus[0], evalStart, evalLen, sr, monoVocalFreqs));
    double virtBlindInstDelta = Math.Abs(DbRatio(MultiToneRms(virtBus[0], evalStart, evalLen, sr, instLFreqs),
                                                 MultiToneRms(dirL, evalStart, evalLen, sr, instLFreqs)));
    double blindParityDelta = Math.Abs(dirBlindVocalAtten - virtBlindVocalAtten);

    // 2B. AI Stereo Stems (wide double-tracked stereo vocal!) on BOTH Direct 2.0 and Virtualizer 7.1.4 (12ch)
    float[] wideL = new float[len], wideR = new float[len];
    for (int i = 0; i < len; i++) { wideL[i] = vocL[i] + instL[i]; wideR[i] = vocR[i] + instR[i]; }
    NeuralSeparatedTrack sepStereo = new() {
        SourceTrackKey = "flac_wide.flac",
        SampleRate = sr,
        Stems = [new StereoStemBuffer("lead_vocal", (float[])vocL.Clone(), (float[])vocR.Clone())],
        MonoLeadVocalRef = (float[])monoVoc.Clone()
    };

    float[][] virtAiBus = new float[12][];
    for (int c = 0; c < 12; c++) virtAiBus[c] = new float[len];
    Array.Copy(wideL, virtAiBus[0], len);
    Array.Copy(wideR, virtAiBus[1], len);
    using (NeuralStemStageProcessor stVirtAi = new()) {
        stVirtAi.SetSourceTrackSampleRate(sr);
        stVirtAi.Configure(sr, SpatialProgramMetadata.CreateDefault());
        stVirtAi.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f });
        stVirtAi.BindSeparatedTrack(sepStereo);
        float[][] blk = new float[12][];
        for (int c = 0; c < 12; c++) blk[c] = new float[1024];
        for (int p = 0; p < len; p += 1024) {
            int n = Math.Min(1024, len - p);
            for (int c = 0; c < 12; c++) Array.Copy(virtAiBus[c], p, blk[c], 0, n);
            stVirtAi.ProcessPreHrtfBus(blk, layout714, 12, n, sr);
            for (int c = 0; c < 12; c++) Array.Copy(blk[c], 0, virtAiBus[c], p, n);
        }
    }
    double virtAiWideAttenL = DbRatio(MultiToneRms(wideL, evalStart, evalLen, sr, wideVocalFreqs),
                                      MultiToneRms(virtAiBus[0], evalStart, evalLen, sr, wideVocalFreqs));
    double virtAiSide620Atten = DbRatio(BandRms(wideL, evalStart, evalLen, sr, 620.0),
                                        BandRms(virtAiBus[0], evalStart, evalLen, sr, 620.0));
    double virtAiInstDeltaL = Math.Abs(DbRatio(MultiToneRms(virtAiBus[0], evalStart, evalLen, sr, instLFreqs),
                                               MultiToneRms(wideL, evalStart, evalLen, sr, instLFreqs)));
    double virtAiInstDeltaR = Math.Abs(DbRatio(MultiToneRms(virtAiBus[1], evalStart, evalLen, sr, instRFreqs),
                                               MultiToneRms(wideR, evalStart, evalLen, sr, instRFreqs)));
    var health = AuditSignalHealth(virtAiBus, 0, len);

    Assert(dirBlindVocalAtten >= 18.0, $"Direct 2.0 blind vocal atten too low: {dirBlindVocalAtten:F2} dB");
    Assert(virtBlindVocalAtten >= 18.0, $"Virtualizer 7.1.4 blind vocal atten too low: {virtBlindVocalAtten:F2} dB");
    Assert(blindParityDelta <= 1.5, $"Direct vs Virtualizer blind parity diverged by {blindParityDelta:F2} dB");
    Assert(dirBlindInstDelta <= 1.5 && virtBlindInstDelta <= 1.5, $"Blind instrument delta exceeded 1.5 dB: dir={dirBlindInstDelta:F2}, virt={virtBlindInstDelta:F2}");
    Assert(virtAiWideAttenL >= 22.0, $"Virtualizer 12ch AI stereo vocal atten too low: {virtAiWideAttenL:F2} dB");
    Assert(virtAiSide620Atten >= 20.0, $"Virtualizer 12ch orthogonal stereo vocal side (620 Hz) failed: {virtAiSide620Atten:F2} dB");
    Assert(virtAiInstDeltaL <= 1.2 && virtAiInstDeltaR <= 1.2, $"AI instrument delta exceeded 1.2 dB: L={virtAiInstDeltaL:F2}, R={virtAiInstDeltaR:F2}");
    Assert(health.NaNs == 0 && health.Clips == 0, $"NaN/Clip detected: NaN={health.NaNs}, Clip={health.Clips}");

    return new Dictionary<string, object> {
        ["dirBlindVocalAttenDb"] = Math.Round(dirBlindVocalAtten, 2),
        ["virtBlindVocalAttenDb"] = Math.Round(virtBlindVocalAtten, 2),
        ["blindParityDeltaDb"] = Math.Round(blindParityDelta, 2),
        ["virtAiWideVocalAttenDb"] = Math.Round(virtAiWideAttenL, 2),
        ["virtAiStereoSide620AttenDb"] = Math.Round(virtAiSide620Atten, 2),
        ["virtAiInstDeltaMaxDb"] = Math.Round(Math.Max(virtAiInstDeltaL, virtAiInstDeltaR), 3)
    };
});

// ---------- GT-03: 60-Second 44.1 kHz -> 48 kHz Playhead Fractional Drift Check ----------
RunSuite("GT-03", "Playhead_44100_to_48000_60s_ZeroDrift_And_LongTermKaraokeLock", () => {
    int trackSr = 44100;
    int outSr = 48000;
    int blockFrames = 1024;
    int totalBlocks = (outSr * 60) / blockFrames; // 60 seconds = 2812 blocks

    // Old buggy integer rounding vs W47 exact double accumulator
    long buggyPos = 0;
    double exactPos = 0.0;
    long fixedPos = 0;
    double step = blockFrames * (trackSr / (double)outSr);
    for (int b = 0; b < totalBlocks; b++) {
        buggyPos += (long)Math.Round(step);
        exactPos += step;
        fixedPos = (long)Math.Round(exactPos);
    }
    double expectedExact = totalBlocks * step;
    double buggyDrift = Math.Abs(buggyPos - expectedExact);
    double fixedDrift = Math.Abs(fixedPos - expectedExact);

    // Also test real 44.1 kHz stem resampled into 48 kHz NeuralStemStageProcessor at t = 2.0s..3.0s
    int len44 = trackSr * 4;
    SynthesizeMultiStem(trackSr, len44, out float[] voc44L, out float[] voc44R, out float[] inst44L, out float[] inst44R, out _);
    KaiserSincResampler res = new(trackSr, outSr);
    float[] mix48L = res.ResampleMono(Enumerable.Range(0, len44).Select(i => voc44L[i] + inst44L[i]).ToArray());
    float[] mix48R = res.ResampleMono(Enumerable.Range(0, len44).Select(i => voc44R[i] + inst44R[i]).ToArray());
    float[] mono44 = Enumerable.Range(0, len44).Select(i => 0.5f * (voc44L[i] + voc44R[i])).ToArray();

    NeuralSeparatedTrack sep44 = new() {
        SourceTrackKey = "flac_44k.flac",
        SampleRate = trackSr,
        Stems = [new StereoStemBuffer("lead_vocal", voc44L, voc44R)],
        MonoLeadVocalRef = mono44
    };

    int len48 = Math.Min(mix48L.Length, mix48R.Length);
    float[] inter = new float[len48 * 2];
    for (int i = 0; i < len48; i++) { inter[i * 2] = mix48L[i]; inter[i * 2 + 1] = mix48R[i]; }
    using (NeuralStemStageProcessor stage = new()) {
        stage.SetSourceTrackSampleRate(trackSr);
        stage.Configure(outSr, SpatialProgramMetadata.CreateDefault());
        stage.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f });
        stage.BindSeparatedTrack(sep44);
        double curTrackExact = 0.0;
        for (int p = 0; p < len48; p += blockFrames) {
            int n = Math.Min(blockFrames, len48 - p);
            long trkPos = (long)Math.Round(curTrackExact);
            stage.ProcessStereoInterleavedKaraoke(inter, p * 2, n, trkPos);
            curTrackExact += n * (trackSr / (double)outSr);
        }
    }
    float[] outL = new float[len48];
    for (int i = 0; i < len48; i++) outL[i] = inter[i * 2];
    double[] wideVocalFreqs = [440.0, 620.0, 880.0, 1320.0];
    double attenDb = DbRatio(MultiToneRms(mix48L, outSr * 2, outSr, outSr, wideVocalFreqs),
                             MultiToneRms(outL, outSr * 2, outSr, outSr, wideVocalFreqs));

    Assert(buggyDrift > 500.0, "Sanity check failed: old rounding bug should have drifted >500 samples.");
    Assert(fixedDrift <= 0.5, $"W47 exact playhead drifted {fixedDrift:0.3} samples over 60s!");
    Assert(attenDb >= 22.0, $"44.1 kHz FLAC -> 48 kHz Karaoke vocal atten too low: {attenDb:0.2} dB");

    return new Dictionary<string, object> {
        ["legacyBuggyDriftSamples60s"] = Math.Round(buggyDrift, 2),
        ["fixedDriftSamples60s"] = Math.Round(fixedDrift, 4),
        ["flac44To48VocalAttenDb"] = Math.Round(attenDb, 2)
    };
});

// ---------- GT-04: Pure Dolby Atmos (7.1.4 Discrete FC + Beds) x Blind & AI Stereo Stems & Offline Export ----------
RunSuite("GT-04", "PureDolbyAtmos_714_DiscreteFC_Blind_AiStereo_And_OfflineExport", () => {
    int sr = 48000;
    int len = sr * 3;
    int evalStart = sr;
    int evalLen = sr;
    double[] wideVocalFreqs = [440.0, 620.0, 880.0, 1320.0];
    double[] instLFreqs = [185.0, 1180.0];

    SynthesizeMultiStem(sr, len, out float[] vocL, out float[] vocR, out float[] instL, out float[] instR, out float[] lfe);
    VirtualChannel[] layout = BuildVirtual714Layout();

    // Build realistic 7.1.4 Atmos bus:
    // FC(2) = discrete lead vocal core + slight wide bleed
    // FL(0) = stereo instrument L + 0.35 * vocL reverb bleed
    // FR(1) = stereo instrument R + 0.35 * vocR reverb bleed
    // LFE(3) = 55 Hz pure sub-bass
    // SL(4)/SR(5)/TFL(8)/TFR(9) = surround/height vocal reverb bleed + ambient instruments
    float[][] MakeAtmos714() {
        float[][] b = new float[12][];
        for (int c = 0; c < 12; c++) b[c] = new float[len];
        for (int i = 0; i < len; i++) {
            float vCore = 0.5f * (vocL[i] + vocR[i]);
            b[0][i] = instL[i] + 0.35f * vocL[i];
            b[1][i] = instR[i] + 0.35f * vocR[i];
            b[2][i] = vCore;
            b[3][i] = lfe[i];
            b[4][i] = 0.25f * instL[i] + 0.20f * vocL[i];
            b[5][i] = 0.25f * instR[i] + 0.20f * vocR[i];
            b[8][i] = 0.18f * vocL[i];
            b[9][i] = 0.18f * vocR[i];
        }
        return b;
    }

    // 4A. Blind Mode on Pure Dolby Atmos 7.1.4 (must isolate discrete FC without FL/FR guitar pollution!)
    float[][] blindBus = MakeAtmos714();
    using (NeuralStemStageProcessor stBlind = new()) {
        stBlind.SetSourceTrackSampleRate(sr);
        stBlind.Configure(sr, SpatialProgramMetadata.CreateDefault());
        stBlind.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f });
        float[][] blk = new float[12][];
        for (int c = 0; c < 12; c++) blk[c] = new float[1024];
        for (int p = 0; p < len; p += 1024) {
            int n = Math.Min(1024, len - p);
            for (int c = 0; c < 12; c++) Array.Copy(blindBus[c], p, blk[c], 0, n);
            stBlind.ProcessPreHrtfBus(blk, layout, 12, n, sr);
            for (int c = 0; c < 12; c++) Array.Copy(blk[c], 0, blindBus[c], p, n);
        }
    }
    double[] monoVocalFreqs = [440.0, 880.0, 1320.0];
    double blindFcAtten = DbRatio(MultiToneRms(MakeAtmos714()[2], evalStart, evalLen, sr, monoVocalFreqs),
                                  MultiToneRms(blindBus[2], evalStart, evalLen, sr, monoVocalFreqs));
    double blindFlInstDelta = Math.Abs(DbRatio(MultiToneRms(blindBus[0], evalStart, evalLen, sr, instLFreqs),
                                               MultiToneRms(instL, evalStart, evalLen, sr, instLFreqs)));
    float blindLfeDiff = 0f;
    for (int i = 0; i < len; i++) blindLfeDiff = MathF.Max(blindLfeDiff, MathF.Abs(blindBus[3][i] - lfe[i]));

    // 4B. AI Stereo Stems (Live Pre-HRTF + Offline ProcessOfflineMultichannel)
    float[][] aiBus = MakeAtmos714();
    float[] monoVoc = Enumerable.Range(0, len).Select(i => 0.5f * (vocL[i] + vocR[i])).ToArray();
    NeuralSeparatedTrack sep = new() {
        SourceTrackKey = "atmos_714.ec3",
        SampleRate = sr,
        Stems = [new StereoStemBuffer("lead_vocal", vocL, vocR)],
        MonoLeadVocalRef = monoVoc
    };
    using (NeuralStemStageProcessor stAi = new()) {
        stAi.SetSourceTrackSampleRate(sr);
        stAi.Configure(sr, SpatialProgramMetadata.CreateDefault());
        stAi.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f });
        stAi.BindSeparatedTrack(sep);
        float[][] blk = new float[12][];
        for (int c = 0; c < 12; c++) blk[c] = new float[1024];
        for (int p = 0; p < len; p += 1024) {
            int n = Math.Min(1024, len - p);
            for (int c = 0; c < 12; c++) Array.Copy(aiBus[c], p, blk[c], 0, n);
            stAi.ProcessPreHrtfBus(blk, layout, 12, n, sr);
            for (int c = 0; c < 12; c++) Array.Copy(blk[c], 0, aiBus[c], p, n);
        }
    }
    double aiFcAtten = DbRatio(MultiToneRms(MakeAtmos714()[2], evalStart, evalLen, sr, monoVocalFreqs),
                               MultiToneRms(aiBus[2], evalStart, evalLen, sr, monoVocalFreqs));
    double aiFlVocalAtten = DbRatio(MultiToneRms(MakeAtmos714()[0], evalStart, evalLen, sr, wideVocalFreqs),
                                    MultiToneRms(aiBus[0], evalStart, evalLen, sr, wideVocalFreqs));
    double aiFlInstDelta = Math.Abs(DbRatio(MultiToneRms(aiBus[0], evalStart, evalLen, sr, instLFreqs),
                                            MultiToneRms(instL, evalStart, evalLen, sr, instLFreqs)));
    double aiTopVocalAtten = DbRatio(MultiToneRms(MakeAtmos714()[8], evalStart, evalLen, sr, wideVocalFreqs),
                                     MultiToneRms(aiBus[8], evalStart, evalLen, sr, wideVocalFreqs));

    // Offline export parity
    float[][] offSrc = MakeAtmos714();
    bool[] isLfe = new bool[12]; isLfe[3] = true;
    bool[] isStrict = new bool[12]; isStrict[2] = true;
    float[][] offOut = SpatialKaraokeEngine.ProcessOfflineMultichannel(
        offSrc, isLfe, vocL, null, sr, 0f, true, 2048, neuralLeadVocalRefRight: vocR, isStrictCenter: isStrict);
    double offFlVocalAtten = DbRatio(MultiToneRms(offSrc[0], evalStart, evalLen, sr, wideVocalFreqs),
                                     MultiToneRms(offOut[0], evalStart, evalLen, sr, wideVocalFreqs));
    float offLfeDiff = 0f;
    for (int i = 0; i < len; i++) offLfeDiff = MathF.Max(offLfeDiff, MathF.Abs(offOut[3][i] - lfe[i]));

    Assert(blindFcAtten >= 18.0, $"Atmos Blind FC vocal atten too low: {blindFcAtten:0.2} dB");
    Assert(blindFlInstDelta <= 1.2, $"Atmos Blind polluted FL instruments by {blindFlInstDelta:0.2} dB!");
    Assert(blindLfeDiff == 0.0f && offLfeDiff == 0.0f, $"Atmos LFE mutated: blind={blindLfeDiff}, off={offLfeDiff}");
    Assert(aiFcAtten >= 22.0 && aiFlVocalAtten >= 22.0 && aiTopVocalAtten >= 22.0,
        $"Atmos AI Stereo vocal atten too low: FC={aiFcAtten:0.2}, FL={aiFlVocalAtten:0.2}, TFL={aiTopVocalAtten:0.2}");
    Assert(aiFlInstDelta <= 1.2, $"Atmos AI FL instrument delta too high: {aiFlInstDelta:0.2} dB");
    Assert(offFlVocalAtten >= 22.0, $"Atmos Offline Stereo vocal atten too low: {offFlVocalAtten:0.2} dB");

    return new Dictionary<string, object> {
        ["blindFcVocalAttenDb"] = Math.Round(blindFcAtten, 2),
        ["blindFlInstDeltaDb"] = Math.Round(blindFlInstDelta, 3),
        ["aiFcVocalAttenDb"] = Math.Round(aiFcAtten, 2),
        ["aiFlStereoVocalAttenDb"] = Math.Round(aiFlVocalAtten, 2),
        ["aiTopStereoVocalAttenDb"] = Math.Round(aiTopVocalAtten, 2),
        ["aiFlInstDeltaDb"] = Math.Round(aiFlInstDelta, 3),
        ["offlineFlStereoVocalAttenDb"] = Math.Round(offFlVocalAtten, 2),
        ["lfeMaxDiff"] = Math.Max(blindLfeDiff, offLfeDiff)
    };
});

// ---------- GT-05: Dolby Atmos + Misaligned FLAC Lossless Twin (+187.35ms, +25ppm, -1.2dB) + ACTIVE LGSR -> Karaoke ----------
RunSuite("GT-05", "Atmos_Plus_MisalignedFlacLosslessTwin_ActiveLGSR_To_Karaoke", () => {
    int sr = 48000;
    int len = sr * 6; // 6s so GCC-PHAT & Theil-Sen have plenty of windows
    int evalStart = sr * 2;
    int evalLen = sr * 2;
    double[] wideVocalFreqs = [440.0, 620.0, 880.0, 1320.0];
    double[] instLFreqs = [185.0, 1180.0];

    SynthesizeMultiStem(sr, len, out float[] vocL, out float[] vocR, out float[] instL, out float[] instR, out float[] lfe);

    // Add broadband pseudo-random musical texture so GCC-PHAT has realistic sharp correlation peaks
    Random rng = new(1337);
    float[] textureL = new float[len];
    float[] textureR = new float[len];
    float lpL = 0f, lpR = 0f;
    for (int i = 0; i < len; i++) {
        lpL = 0.65f * lpL + 0.35f * (float)(rng.NextDouble() * 2.0 - 1.0) * 0.08f;
        lpR = 0.65f * lpR + 0.35f * (float)(rng.NextDouble() * 2.0 - 1.0) * 0.08f;
        textureL[i] = lpL;
        textureR[i] = lpR;
    }

    float[] atmosMixL = new float[len], atmosMixR = new float[len], atmosMono = new float[len];
    for (int i = 0; i < len; i++) {
        atmosMixL[i] = vocL[i] + instL[i] + textureL[i];
        atmosMixR[i] = vocR[i] + instR[i] + textureR[i];
        atmosMono[i] = 0.5f * (atmosMixL[i] + atmosMixR[i]);
    }

    // Create misaligned FLAC Lossless Twin: +187.35 ms (+8992.8 samples) lead-in, +25 ppm drift, -1.2 dB gain
    double trueOffsetSamples = 0.18735 * sr; // 8992.8 samples
    double trueDriftPpm = 25.0;
    double rateScale = 1.0 + trueDriftPpm * 1e-6;
    float gainScale = MathF.Pow(10f, -1.2f / 20f);
    int twinLen = len + 16000;
    float[] twinL = new float[twinLen];
    float[] twinR = new float[twinLen];
    float[] twinVocL = new float[twinLen];
    float[] twinVocR = new float[twinLen];

    for (int j = 0; j < twinLen; j++) {
        // twin[trueOffset + t * rateScale] = gainScale * atmos[t] => t = (j - trueOffset) / rateScale
        double t = (j - trueOffsetSamples) / rateScale;
        int idx = (int)Math.Floor(t);
        if (idx >= 1 && idx + 2 < len) {
            float f = (float)(t - idx);
            twinL[j] = gainScale * (atmosMixL[idx] * (1f - f) + atmosMixL[idx + 1] * f);
            twinR[j] = gainScale * (atmosMixR[idx] * (1f - f) + atmosMixR[idx + 1] * f);
            twinVocL[j] = gainScale * (vocL[idx] * (1f - f) + vocL[idx + 1] * f);
            twinVocR[j] = gainScale * (vocR[idx] * (1f - f) + vocR[idx + 1] * f);
        }
    }

    UniversalLosslessTwinProvider twinProvider = new();
    bool syncedOk = twinProvider.InitializeAndSync(
        new LosslessTwinMetadata { FilePath = "song_lossless_twin.flac", FileName = "song_lossless_twin.flac", NativeSampleRate = sr, EffectiveBitDepth = 24 },
        atmosMono,
        atmosProbeStartSample: 0L,
        atmosSampleRate: sr,
        losslessLeft: twinL,
        losslessRight: twinR,
        losslessSampleRate: sr,
        minConfidenceGate: 0.35f);
    SubSampleSyncResult sync = twinProvider.SyncResult;

    double offsetErr = Math.Abs(sync.BaseOffsetSamples - trueOffsetSamples);
    Assert(syncedOk && sync.IsSynced && sync.Confidence >= 0.70f, $"Twin sync failed: ok={syncedOk}, synced={sync.IsSynced}, conf={sync.Confidence:0.3}");
    Assert(offsetErr <= 1.0, $"Twin sub-sample alignment error too large: {offsetErr:0.3} samples");

    // Extract aligned vocal stem (simulating ONNX running on TryExtractFullAlignedStereo!)
    float[] alignedVocL = new float[len];
    float[] alignedVocR = new float[len];
    Assert(FarrowLagrangeDelay.ReadAlignedStereoBlock(twinVocL, twinVocR, 0L, sync.BaseOffsetSamples, sync.DriftPpm, alignedVocL, alignedVocR, len), "Aligned vocal extraction failed.");
    NeuralSeparatedTrack twinSep = new() {
        SourceTrackKey = "atmos_with_twin.ec3",
        SampleRate = sr,
        DerivedFromLosslessTwin = true,
        Stems = [new StereoStemBuffer("lead_vocal", alignedVocL, alignedVocR)],
        MonoLeadVocalRef = Enumerable.Range(0, len).Select(i => 0.5f * (alignedVocL[i] + alignedVocR[i])).ToArray()
    };

    // Build 12ch Pre-HRTF Atmos bus and run ACTIVE LGSR (512-sample WOLA delay) IMMEDIATELY BEFORE NeuralStemStageProcessor!
    VirtualChannel[] layout = BuildVirtual714Layout();
    float[][] bus = new float[12][];
    for (int c = 0; c < 12; c++) bus[c] = new float[len];
    for (int i = 0; i < len; i++) {
        bus[0][i] = instL[i] + textureL[i] + 0.5f * vocL[i];
        bus[1][i] = instR[i] + textureR[i] + 0.5f * vocR[i];
        bus[2][i] = 0.5f * (vocL[i] + vocR[i]);
        bus[3][i] = lfe[i];
    }

    using LgsrStageProcessor lgsrStage = new();
    using NeuralStemStageProcessor karaokeStage = new();
    SpatialProgramMetadata lossyAtmosMeta = new() {
        IsLossySpatialStream = true,
        CouplingStartHz = 3500f,
        SpxStartHz = 10000f
    };
    lgsrStage.BindLosslessReference(twinProvider);
    lgsrStage.SetActiveTrackLosslessStatus(false);
    lgsrStage.ApplyConfig(new LgsrConfig { Enabled = true, FusionStrength = 0.85f });
    lgsrStage.Configure(sr, lossyAtmosMeta);

    karaokeStage.SetSourceTrackSampleRate(sr);
    karaokeStage.Configure(sr, lossyAtmosMeta);
    karaokeStage.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f });
    karaokeStage.BindLosslessReference(twinProvider);
    karaokeStage.BindSeparatedTrack(twinSep);
    // W47 FIX: Live dynamic LGSR WOLA latency provider!
    karaokeStage.UpstreamLatencySamplesProvider = () => lgsrStage.LastAppliedLatencySamples;

    SpatialSceneFrame frame = new();
    float[] dummyStereo = new float[2048];
    float[][] blk = new float[12][];
    for (int c = 0; c < 12; c++) blk[c] = new float[1024];

    for (int p = 0; p < len; p += 1024) {
        int n = Math.Min(1024, len - p);
        frame.BeginBlock(sr, n, p, 0L, new Vector3(6, 3, 6), Environments.Home, true,
            ListenerPose6DoF.Identity, lossyAtmosMeta, Array.Empty<SpatialSourceDescriptor>(), dummyStereo, 0, 2);
        lgsrStage.Process(frame);
        karaokeStage.Process(frame);

        for (int c = 0; c < 12; c++) Array.Copy(bus[c], p, blk[c], 0, n);
        // Exact RealtimePlayerEngine Pre-HRTF execution order: LGSR first -> Karaoke second!
        lgsrStage.ProcessPreHrtfBus(blk, layout, 12, n, sr);
        karaokeStage.ProcessPreHrtfBus(blk, layout, 12, n, sr);
        for (int c = 0; c < 12; c++) Array.Copy(blk[c], 0, bus[c], p, n);
    }

    Assert(lgsrStage.LastAppliedLatencySamples == 512, $"Expected LGSR applied latency 512, got {lgsrStage.LastAppliedLatencySamples}");

    // Account for 512-sample LGSR + 1024-sample Karaoke causality when measuring steady-state tones
    double fcVocBefore = MultiToneRms(Enumerable.Range(0, len).Select(i => 0.5f * (vocL[i] + vocR[i])).ToArray(), evalStart, evalLen, sr, [440.0, 880.0, 1320.0]);
    double fcVocAfter = MultiToneRms(bus[2], evalStart, evalLen, sr, [440.0, 880.0, 1320.0]);
    double fcAttenDb = DbRatio(fcVocBefore, fcVocAfter);

    double flVocBefore = MultiToneRms(Enumerable.Range(0, len).Select(i => instL[i] + textureL[i] + 0.5f * vocL[i]).ToArray(), evalStart, evalLen, sr, wideVocalFreqs);
    double flVocAfter = MultiToneRms(bus[0], evalStart, evalLen, sr, wideVocalFreqs);
    double flAttenDb = DbRatio(flVocBefore, flVocAfter);

    double flInstDeltaDb = Math.Abs(DbRatio(MultiToneRms(bus[0], evalStart, evalLen, sr, instLFreqs),
                                            MultiToneRms(instL, evalStart, evalLen, sr, instLFreqs)));
    float lfeDiff = 0f;
    for (int i = 0; i < len; i++) lfeDiff = MathF.Max(lfeDiff, MathF.Abs(bus[3][i] - lfe[i]));
    var health = AuditSignalHealth(bus, 0, len);

    Assert(fcAttenDb >= 20.0, $"LGSR+Karaoke Atmos+Twin FC vocal atten failed: {fcAttenDb:0.2} dB (latency misalignment!)");
    Assert(flAttenDb >= 20.0, $"LGSR+Karaoke Atmos+Twin FL stereo vocal atten failed: {flAttenDb:0.2} dB");
    Assert(flInstDeltaDb <= 1.5, $"LGSR+Karaoke Atmos+Twin FL instrument delta too high: {flInstDeltaDb:0.2} dB");
    Assert(lfeDiff == 0.0f, $"LGSR+Karaoke modified LFE: maxDiff={lfeDiff}");
    Assert(health.NaNs == 0 && health.Clips == 0, $"Signal health failed: NaN={health.NaNs}, Clip={health.Clips}");

    return new Dictionary<string, object> {
        ["syncOffsetErrorSamples"] = Math.Round(offsetErr, 3),
        ["syncConfidence"] = Math.Round(sync.Confidence, 3),
        ["lgsrCompensatedLatencySamples"] = lgsrStage.LastAppliedLatencySamples,
        ["fcVocalAttenDb"] = Math.Round(fcAttenDb, 2),
        ["flStereoVocalAttenDb"] = Math.Round(flAttenDb, 2),
        ["flInstDeltaDb"] = Math.Round(flInstDeltaDb, 3),
        ["lfeMaxDiff"] = lfeDiff
    };
});

// ---------- GT-06: W46 3-Stage Async Pipeline 0.0 dB Bit-Exact Parity ----------
RunSuite("GT-06", "Async3StagePipeline_BitExact_0dB_Parity", () => {
    int sr = 44100;
    int len = 40000;
    float[] mixL = new float[len], mixR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        mixL[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * t) + 0.2 * Math.Cos(2 * Math.PI * 990 * t));
        mixR[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 330 * t + 0.2) + 0.25 * Math.Sin(2 * Math.PI * 770 * t));
    }
    MvsepPipelineOptions opt = new() {
        ChunkSamples = 16384,
        OverlapFactor = 2,
        EnableTta = true,
        EnablePhaseFix = true,
        EnforceEnergyConservation = true,
        EnableAsyncPipelining = true,
        GpuUiBreathingYieldMs = 0
    };
    StereoStemBuffer[] seq = MvsepAntiArtifactPipeline.ProcessTrack(mixL, mixR, (l, r, n) => {
        float[] vL = new float[n], vR = new float[n], iL = new float[n], iR = new float[n];
        for (int k = 0; k < n; k++) {
            float m = 0.5f * (l[k] + r[k]);
            vL[k] = m * 0.55f; vR[k] = m * 0.55f;
            iL[k] = l[k] - vL[k]; iR[k] = r[k] - vR[k];
        }
        return [new StereoStemBuffer("vocals", vL, vR), new StereoStemBuffer("instrumental", iL, iR)];
    }, opt);
    StereoStemBuffer[] pipe = MvsepAntiArtifactPipeline.ProcessTrackPipelined(
        mixL, mixR,
        (l, r, n) => ((float[])l.Clone(), (float[])r.Clone(), n),
        p => {
            int n = p.n;
            float[] vL = new float[n], vR = new float[n], iL = new float[n], iR = new float[n];
            for (int k = 0; k < n; k++) {
                float m = 0.5f * (p.Item1[k] + p.Item2[k]);
                vL[k] = m * 0.55f; vR[k] = m * 0.55f;
                iL[k] = p.Item1[k] - vL[k]; iR[k] = p.Item2[k] - vR[k];
            }
            return (vL, vR, iL, iR);
        },
        (p, inf, n) => [new StereoStemBuffer("vocals", inf.vL, inf.vR), new StereoStemBuffer("instrumental", inf.iL, inf.iR)],
        opt);

    float maxDiff = 0f;
    for (int s = 0; s < seq.Length; s++) {
        for (int i = 0; i < seq[s].Length; i++) {
            maxDiff = MathF.Max(maxDiff, MathF.Abs(seq[s].Left[i] - pipe[s].Left[i]));
            maxDiff = MathF.Max(maxDiff, MathF.Abs(seq[s].Right[i] - pipe[s].Right[i]));
        }
    }
    Assert(maxDiff == 0.0f, $"Async pipeline parity diff={maxDiff}");
    return new Dictionary<string, object> { ["maxAbsDiff"] = maxDiff };
});

// ---------- GT-07: SpatialXRayPowermap Temporal Smoothing ----------
RunSuite("GT-07", "SpatialXRayPowermap_TemporalSmoothing_ZeroThrashing", () => {
    SpatialXRayPowermap pm = new();
    SpatialSourceDescriptor leftSrc = new() {
        Index = 0,
        WorldPositionMeters = new Vector3(-2f, 0f, 0.5f),
        RmsLevel = 0.8f,
        MidBandEnergy = 0.7f
    };
    SpatialSourceDescriptor rightSrc = new() {
        Index = 0,
        WorldPositionMeters = new Vector3(2f, 0f, 0.5f),
        RmsLevel = 0.8f,
        MidBandEnergy = 0.7f
    };
    for (int f = 0; f < 15; f++) {
        pm.Update([leftSrc], Vector3.Zero, temporalSmoothing: 0.78f);
    }
    float[] linGrid0 = (float[])pm.MidEnergyGrid.Clone();
    Vector3 dir0 = pm.DominantDirection;
    pm.Update([rightSrc], Vector3.Zero, temporalSmoothing: 0.78f);
    float[] linGrid1 = (float[])pm.MidEnergyGrid.Clone();
    Vector3 dir1 = pm.DominantDirection;
    for (int f = 0; f < 30; f++) {
        pm.Update([rightSrc], Vector3.Zero, temporalSmoothing: 0.78f);
    }
    float[] linGridFinal = (float[])pm.MidEnergyGrid.Clone();
    Vector3 dirFinal = pm.DominantDirection;
    int rightBin = 0;
    float best = -1f;
    for (int i = 0; i < linGridFinal.Length; i++) {
        if (linGridFinal[i] > best) { best = linGridFinal[i]; rightBin = i; }
    }
    float totalDelta = MathF.Max(1e-6f, linGridFinal[rightBin] - linGrid0[rightBin]);
    float firstStepFrac = (linGrid1[rightBin] - linGrid0[rightBin]) / totalDelta;
    float dirStepDot = Vector3.Dot(dir0, dir1);
    Assert(firstStepFrac > 0.10f && firstStepFrac < 0.35f,
        $"Expected smooth linear temporal convergence (0.10..0.35 on frame 1), got {firstStepFrac:F3}");
    Assert(dirStepDot > 0.75f && dirFinal.X > 0.6f,
        $"DominantDirection thrashed or failed to converge: stepDot={dirStepDot:F3}, finalX={dirFinal.X:F3}");
    return new Dictionary<string, object> {
        ["firstFrameLinearJumpFraction"] = Math.Round(firstStepFrac, 3),
        ["dominantDirStepDot"] = Math.Round(dirStepDot, 3),
        ["dominantDirFinalX"] = Math.Round(dirFinal.X, 3),
        ["rightBinPeak"] = Math.Round(linGridFinal[rightBin], 3)
    };
});

// ---------- iamf-roundtrip: 7.1.4 & 16ch HOA .iamf OBU Layout + Decoder + AudioFile Routing ----------
RunSuite("iamf-roundtrip", "IAMF_714_And_HOA16_OBU_Spec_Decoder_And_AudioFile_Routing", () => {
    string outDir = string.IsNullOrEmpty(iamfOutDir)
        ? Path.Combine(Path.GetTempPath(), "cavern_iamf_gt_" + Guid.NewGuid().ToString("N")[..8])
        : iamfOutDir;
    Directory.CreateDirectory(outDir);
    string path714 = Path.Combine(outDir, "iamf_714.iamf");
    string pathHoa = Path.Combine(outDir, "iamf_hoa16.iamf");
    string pathAmbCopy = Path.Combine(outDir, "iamf_hoa16.amb");

    if (Listener.EnvironmentSize.LengthSquared() < 1f) Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
    int sr = 48000;
    float[] monoTone = new float[2048];
    for (int i = 0; i < monoTone.Length; i++) {
        monoTone[i] = 0.65f * MathF.Sin(2f * MathF.PI * 440f * i / sr);
    }
    Clip toneClip = new(monoTone, 1, sr);

    // 1. 7.1.4 Channel-Based IAMF (12 ch)
    Listener l714 = new(false) { SampleRate = sr, UpdateRate = 1024, MaximumSources = 16, Normalizer = 0f };
    ReferenceChannel[] bed714 = ChannelPrototype.GetStandardMatrix(12);
    for (int i = 0; i < 12; i++) {
        l714.AttachSource(new Source {
            Clip = toneClip,
            Loop = true,
            Position = ChannelPrototype.AlternativePositions[(int)bed714[i]] * Listener.EnvironmentSize,
            VolumeRolloff = Rolloffs.Disabled
        });
    }
    using (IamfEnvironmentWriter w714 = new(path714, l714, 2048, BitDepth.Int24, useSceneBasedHoa: false, hoaOrder: 3, mixLabel: "GT_714")) {
        w714.WriteNextFrame();
        w714.WriteNextFrame();
    }

    // 2. 16ch 3rd-Order HOA Scene-Based IAMF
    Listener lHoa = new(false) { SampleRate = sr, UpdateRate = 1024, MaximumSources = 16, Normalizer = 0f };
    lHoa.AttachSource(new Source { Clip = toneClip, Loop = true, Position = new Vector3(2f, 0f, 3f), VolumeRolloff = Rolloffs.Disabled });
    lHoa.AttachSource(new Source { Clip = toneClip, Loop = true, Position = new Vector3(-2f, 1.5f, -2f), VolumeRolloff = Rolloffs.Disabled });
    using (IamfEnvironmentWriter wHoa = new(pathHoa, lHoa, 2048, BitDepth.Int24, useSceneBasedHoa: true, hoaOrder: 3, mixLabel: "GT_HOA16")) {
        wHoa.WriteNextFrame();
        wHoa.WriteNextFrame();
    }
    File.Copy(pathHoa, pathAmbCopy, overwrite: true);

    static ulong ReadUleb128(byte[] data, ref int idx) {
        ulong val = 0; int shift = 0;
        while (idx < data.Length) {
            byte b = data[idx++];
            val |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return val;
            shift += 7;
        }
        throw new Exception("Truncated uleb128 in IAMF OBU.");
    }

    static (int obuCount, bool hasAmbModeZero, bool hasMixGain101And100, bool hasTruePeak) InspectIamfObus(byte[] bytes, bool expectSceneBased, int expectedCh) {
        int pos = 0, obuCount = 0;
        bool hasSeq = false, hasCodec = false, hasAudioElem = false, hasMix = false;
        bool hasAmbModeZero = false, hasMixGain101And100 = false, hasTruePeak = false;
        while (pos < bytes.Length) {
            byte hdr = bytes[pos++];
            int obuType = (hdr >> 3) & 0x1F;
            ulong sz = ReadUleb128(bytes, ref pos);
            int payloadStart = pos;
            int payloadEnd = pos + (int)sz;
            obuCount++;
            if (obuType == (int)IamfObuType.SequenceHeader) {
                hasSeq = sz >= 6 && Encoding.ASCII.GetString(bytes, payloadStart, 4) == "iamf";
            } else if (obuType == (int)IamfObuType.CodecConfig) {
                hasCodec = sz >= 8;
            } else if (obuType == (int)IamfObuType.AudioElement) {
                hasAudioElem = true;
                int p = payloadStart;
                _ = ReadUleb128(bytes, ref p); // audio_element_id
                byte aeType = (byte)((bytes[p++] >> 5) & 0x07);
                _ = ReadUleb128(bytes, ref p); // codec_config_id
                ulong numSub = ReadUleb128(bytes, ref p);
                for (ulong s = 0; s < numSub; s++) _ = ReadUleb128(bytes, ref p);
                ulong numParam = ReadUleb128(bytes, ref p);
                if (numParam != 0) throw new Exception($"Expected num_parameters=0, got {numParam}");
                if (expectSceneBased) {
                    if (aeType != 1) throw new Exception($"Expected scene-based aeType=1, got {aeType}");
                    ulong ambMode = ReadUleb128(bytes, ref p); // ambisonics_mode BEFORE output_channel_count
                    byte outCh = bytes[p++];
                    byte subCnt = bytes[p++];
                    hasAmbModeZero = ambMode == 0 && outCh == expectedCh && subCnt == expectedCh;
                    for (int c = 0; c < expectedCh; c++) {
                        if (bytes[p++] != c) throw new Exception($"Channel mapping mismatch at {c}");
                    }
                }
            } else if (obuType == (int)IamfObuType.MixPresentation) {
                hasMix = true;
                // Scan payload for element_mix_gain (101) & output_mix_gain (100) paramIds + TruePeak info_type (0x01)
                int p = payloadStart;
                _ = ReadUleb128(bytes, ref p); // mix_presentation_id
                ulong cntLbl = ReadUleb128(bytes, ref p);
                for (ulong i = 0; i < cntLbl; i++) while (p < payloadEnd && bytes[p++] != 0) { }
                for (ulong i = 0; i < cntLbl; i++) while (p < payloadEnd && bytes[p++] != 0) { }
                _ = ReadUleb128(bytes, ref p); // num_sub_mixes
                _ = ReadUleb128(bytes, ref p); // num_audio_elements
                _ = ReadUleb128(bytes, ref p); // audio_element_id
                while (p < payloadEnd && bytes[p++] != 0) { } // localized_element_annotations
                p++; // headphones_rendering_mode
                ulong extSz = ReadUleb128(bytes, ref p);
                p += (int)extSz;
                ulong elemParamId = ReadUleb128(bytes, ref p); // element_mix_gain = 101
                _ = ReadUleb128(bytes, ref p); p += 3;
                ulong outParamId = ReadUleb128(bytes, ref p); // output_mix_gain = 100
                _ = ReadUleb128(bytes, ref p); p += 3;
                hasMixGain101And100 = elemParamId == 101 && outParamId == 100;
                _ = ReadUleb128(bytes, ref p); // num_layouts
                p++; // sound_system
                byte infoType = bytes[p++];
                hasTruePeak = (infoType & 0x01) != 0;
            }
            pos = payloadEnd;
        }
        if (!hasSeq || !hasCodec || !hasAudioElem || !hasMix) throw new Exception("Missing required IAMF descriptor OBUs.");
        return (obuCount, hasAmbModeZero, hasMixGain101And100, hasTruePeak);
    }

    var info714 = InspectIamfObus(File.ReadAllBytes(path714), expectSceneBased: false, expectedCh: 12);
    var infoHoa = InspectIamfObus(File.ReadAllBytes(pathHoa), expectSceneBased: true, expectedCh: 16);
    Assert(infoHoa.hasAmbModeZero, "HOA AudioElement must encode ambisonics_mode=0 uleb128 before channel counts.");
    Assert(info714.hasMixGain101And100 && infoHoa.hasMixGain101And100, "MixPresentation must define paramIds 101 & 100.");
    Assert(info714.hasTruePeak && infoHoa.hasTruePeak, "MixPresentation must encode True Peak loudness metadata.");

    // 3. IamfDecoder roundtrip + AudioFile .iamf/.amb routing
    float maxPeak714 = 0f, maxPeakHoa = 0f;
    using (FileStream fs714 = File.OpenRead(path714)) {
        IamfDecoder d714 = new(fs714);
        Assert(d714.ChannelCount == 12 && d714.SampleRate == sr, $"7.1.4 IamfDecoder mismatch: ch={d714.ChannelCount}");
        float[] pcm = new float[2048 * 12];
        d714.DecodeBlock(pcm, 0, pcm.Length);
        foreach (float s in pcm) maxPeak714 = MathF.Max(maxPeak714, MathF.Abs(s));
    }
    using (FileStream fsHoa = File.OpenRead(pathHoa)) {
        IamfDecoder dHoa = new(fsHoa);
        Assert(dHoa.ChannelCount == 16 && dHoa.SampleRate == sr && dHoa.Info.ElementType == IamfAudioElementType.SceneBased,
            $"HOA16 IamfDecoder mismatch: ch={dHoa.ChannelCount}, type={dHoa.Info.ElementType}");
        float[] pcm = new float[2048 * 16];
        dHoa.DecodeBlock(pcm, 0, pcm.Length);
        foreach (float s in pcm) maxPeakHoa = MathF.Max(maxPeakHoa, MathF.Abs(s));
    }
    Assert(maxPeak714 > 0.05f && maxPeak714 <= 0.895f, $"7.1.4 IAMF headroom/signal out of range: peak={maxPeak714}");
    Assert(maxPeakHoa > 0.05f && maxPeakHoa <= 0.895f, $"HOA16 IAMF headroom/signal out of range: peak={maxPeakHoa}");

    using (AudioFile af714 = new(path714)) Assert(af714.Tracks.Count == 1 && af714.Tracks[0].Codec == Codec.IAMF, "AudioFile .iamf routing failed.");
    using (AudioFile afAmb = new(pathAmbCopy)) Assert(afAmb.Tracks.Count == 1 && afAmb.Tracks[0].Codec == Codec.IAMF, "AudioFile .amb IAMF routing failed.");

    return new Dictionary<string, object> {
        ["obuCount714"] = info714.obuCount,
        ["obuCountHoa16"] = infoHoa.obuCount,
        ["maxPeak714"] = Math.Round(maxPeak714, 4),
        ["maxPeakHoa16"] = Math.Round(maxPeakHoa, 4),
        ["ambisonicsModeZeroBeforeChCounts"] = infoHoa.hasAmbModeZero,
        ["mixGain101_100_TruePeak"] = infoHoa.hasMixGain101And100 && infoHoa.hasTruePeak
    };
});

// ---------- proaudio-route-fsm (C# Scarlett2Protocol constants & FCP table check) ----------
RunSuite("proaudio-route-fsm", "ProAudio_Scarlett2Protocol_Constants_And_PreAmp", () => {
    Assert(Scarlett2Protocol.VendorIdFocusrite == 0x1235, "Focusrite VID mismatch.");
    Assert(Scarlett2Protocol.PidScarlettSoloGen4 == 0x8218 && Scarlett2Protocol.PidScarlett2i2Gen4 == 0x8219 && Scarlett2Protocol.PidScarlett4i4Gen4 == 0x821A,
        "Gen 4 Scarlett PID constants mismatch.");
    Assert(Scarlett2Protocol.CmdDataActivate == 0x00800002u && Scarlett2Protocol.CmdInfoDevmap == 0x0080000Cu,
        "Scarlett2 FCP command IDs mismatch.");
    Assert(HardwarePreAmpStateSnapshot.MicSourcePhone == 0 && HardwarePreAmpStateSnapshot.MicSourceUsbInterface == 1,
        "MicSource constants mismatch.");
    return new Dictionary<string, object> {
        ["vendorId"] = Scarlett2Protocol.VendorIdFocusrite,
        ["soloGen4Pid"] = Scarlett2Protocol.PidScarlettSoloGen4,
        ["cmdDataActivate"] = Scarlett2Protocol.CmdDataActivate
    };
});

// ---------- spatial-cortex-rt: 3D Geodesic ADM Decimation + ZeroAlloc + Bass XCorr + BS.1770 ----------
RunSuite("spatial-cortex-rt", "SpatialCortex_3DGeodesicADM_ZeroAlloc_BassXCorr_And_TruePeak", () => {
    if (Listener.EnvironmentSize.LengthSquared() < 1f) Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
    int sr = 48000;
    int updateRate = 1024;

    // 1. 3D Geodesic ADM decimation across +/-180 deg wrap & el=90 deg zenith + jumpPosition (>60 deg cut)
    string admPath = Path.Combine(Path.GetTempPath(), $"cavern_adm_geodesic_{Guid.NewGuid():N}.wav");
    try {
        Listener admListener = new(false) { SampleRate = sr, UpdateRate = updateRate, MaximumSources = 4, Normalizer = 0f };
        Source movingObj = new() { VolumeRolloff = Rolloffs.Disabled };
        admListener.AttachSource(movingObj);

        // 24 smooth steps across rear +/-180 deg wrap & el=90 zenith (<= 0.35 deg/step) + 1 discontinuous 180 deg jump
        Vector3[] path = new Vector3[26];
        for (int i = 0; i < 12; i++) {
            // Azimuth +178 -> -178 deg through 180 deg wrap (0.36 deg/step, decimation merges collinear arcs <= 0.5 deg)
            double azRad = (178.0 + i * 0.36) * Math.PI / 180.0;
            path[i] = new Vector3((float)Math.Sin(azRad), 0f, (float)Math.Cos(azRad));
        }
        for (int i = 0; i < 12; i++) {
            // Zenith sweep across el=90 deg pole: x from +0.025 to -0.025 at y=1
            float x = 0.025f - i * (0.05f / 11f);
            Vector3 u = Vector3.Normalize(new Vector3(x, 1f, 0.001f));
            path[12 + i] = u;
        }
        path[24] = new Vector3(0f, 0f, 1f);
        path[25] = new Vector3(0f, 0f, -1f); // 180 deg discontinuous jump -> must emit jumpPosition="1"

        double maxGeodesicStepErrorDeg = 0.0;
        double maxNaivePolarWrapDeg = 0.0;
        using (BroadcastWaveFormatWriter bwWriter = new(admPath, admListener, path.Length * updateRate, BitDepth.Int16)) {
            for (int f = 0; f < path.Length; f++) {
                movingObj.Position = path[f] * Listener.EnvironmentSize;
                bwWriter.WriteNextFrame();
                if (f > 0 && f < 12) {
                    Vector3 a = Vector3.Normalize(path[f - 1]);
                    Vector3 b = Vector3.Normalize(path[f]);
                    double geoDeg = Math.Acos(Math.Clamp((double)Vector3.Dot(a, b), -1.0, 1.0)) * 180.0 / Math.PI;
                    maxGeodesicStepErrorDeg = Math.Max(maxGeodesicStepErrorDeg, geoDeg);
                    double azA = Math.Atan2(a.X, a.Z) * 180.0 / Math.PI;
                    double azB = Math.Atan2(b.X, b.Z) * 180.0 / Math.PI;
                    maxNaivePolarWrapDeg = Math.Max(maxNaivePolarWrapDeg, Math.Abs(azB - azA));
                }
            }
        }
        string admBytesLatin1 = Encoding.Latin1.GetString(File.ReadAllBytes(admPath));
        int axmlIdx = admBytesLatin1.IndexOf("<audioFormatExtended", StringComparison.Ordinal);
        Assert(axmlIdx >= 0, "Expected ADM <audioFormatExtended> axml chunk in BWF WAV.");
        string axml = admBytesLatin1[axmlIdx..];
        int blockCount = axml.Split("<audioBlockFormat").Length - 1;
        Assert(blockCount < path.Length, $"Expected 3D geodesic decimation (< {path.Length} blocks), got {blockCount}");
        Assert(System.Text.RegularExpressions.Regex.IsMatch(axml, @"<jumpPosition\b[^>]*>1</jumpPosition>"),
            "Expected <jumpPosition>1</jumpPosition> on 180 deg discontinuous cut.");
        Assert(maxGeodesicStepErrorDeg <= 0.5 && maxNaivePolarWrapDeg > 180.0,
            $"Expected geodesic <=0.5 deg vs polar wrap >180 deg: geo={maxGeodesicStepErrorDeg:0.3}, polar={maxNaivePolarWrapDeg:0.1}");

        // 2. EvaluatePositionZeroAlloc 0 B heap alloc + slew <= 60 deg/s + LeadVocal |az|<=22 deg
        int len = sr * 8;
        float[] vocL = new float[len], vocR = new float[len], basL = new float[len], basR = new float[len], oL = new float[len], oR = new float[len];
        for (int i = 0; i < len; i++) {
            double t = i / (double)sr;
            vocL[i] = vocR[i] = 0.45f * MathF.Sin((float)(2 * Math.PI * 440 * t));
            basL[i] = basR[i] = 0.50f * MathF.Sin((float)(2 * Math.PI * 80 * t));
            oL[i] = 0.35f * MathF.Sin((float)(2 * Math.PI * 1100 * t));
            oR[i] = 0.35f * MathF.Cos((float)(2 * Math.PI * 1300 * t));
        }
        StereoStemBuffer[] stems = [new("vocals", vocL, vocR), new("bass", basL, basR), new("other", oL, oR)];
        DirectorCue[] cues = Layer1AudioForensics.Analyze(stems, sr, SpatialChoreoMode.Cinematic);
        SpatialChoreoTrack choreo = SpatialChoreoTrack.Build(["vocals", "bass", "other"], cues, SpatialChoreoMode.Cinematic, 8.0);
        int vIdx = choreo.FindStemIndex(NeuralStemRole.LeadVocal, "vocals");
        int bIdx = choreo.FindStemIndex(NeuralStemRole.Bass, "bass");
        choreo.EvaluatePositionZeroAlloc(vIdx, 0.5, out _);
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        Vector3 prevV = Vector3.Zero;
        double maxSlewMeasured = 0.0;
        float maxVocalAz = 0f;
        for (int k = 0; k < 4000; k++) {
            double t = k * 0.002;
            choreo.EvaluatePositionZeroAlloc(vIdx, t, out Vector3 vp);
            PsychoacousticGovernor.CartesianToSphericalDeg(vp, out float vAz, out _);
            maxVocalAz = MathF.Max(maxVocalAz, MathF.Abs(vAz));
            Vector3 vn = Vector3.Normalize(vp);
            if (k > 0) {
                double ang = Math.Acos(Math.Clamp((double)Vector3.Dot(prevV, vn), -1.0, 1.0)) * 180.0 / Math.PI;
                maxSlewMeasured = Math.Max(maxSlewMeasured, ang / 0.002);
            }
            prevV = vn;
        }
        long alloc1 = GC.GetAllocatedBytesForCurrentThread();
        long zeroAllocDelta = alloc1 - alloc0;
        Assert(zeroAllocDelta == 0, $"EvaluatePositionZeroAlloc allocated {zeroAllocDelta} bytes.");
        Assert(maxVocalAz <= 22.05f, $"LeadVocal azimuth exceeded 22 deg: {maxVocalAz:0.2}");
        Assert(maxSlewMeasured <= 62.0, $"Slew exceeded 60 deg/s ceiling: {maxSlewMeasured:0.2}");

        // 3. Bass <120Hz phase-coherence xcorr >= 0.95 (FL vs FR on 80Hz bass stem)
        float[][] bus12 = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe([new StereoStemBuffer("bass", basL, basR)], sr);
        float[][] raw11 = NeuralStemSpatializer.ProcessHybridNeuralDirAc714([new StereoStemBuffer("bass", basL, basR)], sr);
        double dot = 0.0, e0 = 0.0, e1 = 0.0;
        for (int i = 0; i < len; i++) {
            double a = raw11[0][i], b = raw11[1][i];
            dot += a * b; e0 += a * a; e1 += b * b;
        }
        double bassXcorr = dot / Math.Sqrt(Math.Max(1e-12, e0 * e1));
        Assert(bassXcorr >= 0.95, $"Bass <120Hz FL/FR xcorr must be >=0.95, got {bassXcorr:0.4}");

        // 4. BS.1770 -1.0 dBTP True-Peak clamp
        PsychoacousticGovernor.ClampBlockHeadroomBs1770(bus12, len);
        float peakBus = 0f;
        foreach (float[] ch in bus12) foreach (float s in ch) peakBus = MathF.Max(peakBus, MathF.Abs(s));
        float peakDbtp = 20f * MathF.Log10(MathF.Max(1e-9f, peakBus));
        Assert(peakDbtp <= -0.99f, $"Expected <= -1.0 dBTP headroom, got {peakDbtp:0.3} dBTP");

        return new Dictionary<string, object> {
            ["admDecimatedBlockCount"] = blockCount,
            ["admTotalSteps"] = path.Length,
            ["maxGeodesicStepDeg"] = Math.Round(maxGeodesicStepErrorDeg, 3),
            ["maxNaivePolarWrapDeg"] = Math.Round(maxNaivePolarWrapDeg, 1),
            ["zeroAllocBytes"] = zeroAllocDelta,
            ["maxLeadVocalAzDeg"] = Math.Round(maxVocalAz, 2),
            ["maxSlewDegPerSec"] = Math.Round(maxSlewMeasured, 2),
            ["bassSub120HzXcorr"] = Math.Round(bassXcorr, 4),
            ["clampedPeakDbtp"] = Math.Round(peakDbtp, 3)
        };
    } finally {
        try { if (File.Exists(admPath)) File.Delete(admPath); } catch { }
    }
});

// =========================================================================
// GT-11: Pure Instrumental (Remove Backing Vocals) vs Keep Backing Ground Truth
// =========================================================================
RunSuite("GT-11", "PureInstrumental_LeadAndBackingRemoval_ZeroInstrumentDamage", () => {
    const int sr = 48000;
    const int len = sr * 2; // 2.0 s
    float[] leadL = new float[len], leadR = new float[len];
    float[] backL = new float[len], backR = new float[len];
    float[] instL = new float[len], instR = new float[len];
    float[] vocAllL = new float[len], vocAllR = new float[len];
    float[] mixL = new float[len], mixR = new float[len];

    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        leadL[i] = leadR[i] = 0.28f * MathF.Sin((float)(2 * Math.PI * 440.0 * t)) + 0.10f * MathF.Sin((float)(2 * Math.PI * 880.0 * t));
        backL[i] = 0.20f * MathF.Sin((float)(2 * Math.PI * 660.0 * t)) + 0.08f * MathF.Sin((float)(2 * Math.PI * 990.0 * t));
        backR[i] = 0.20f * MathF.Cos((float)(2 * Math.PI * 660.0 * t)) + 0.08f * MathF.Cos((float)(2 * Math.PI * 990.0 * t));
        instL[i] = 0.24f * MathF.Sin((float)(2 * Math.PI * 185.0 * t)) + 0.14f * MathF.Sin((float)(2 * Math.PI * 1180.0 * t));
        instR[i] = 0.24f * MathF.Cos((float)(2 * Math.PI * 185.0 * t)) + 0.14f * MathF.Sin((float)(2 * Math.PI * 1640.0 * t));
        vocAllL[i] = leadL[i] + backL[i];
        vocAllR[i] = leadR[i] + backR[i];
        mixL[i] = vocAllL[i] + instL[i];
        mixR[i] = vocAllR[i] + instR[i];
    }

    NeuralSeparatedTrack track = new() {
        SourceTrackKey = "gt11_pure_instrumental",
        SampleRate = sr,
        Stems = [
            new StereoStemBuffer("lead_vocal", leadL, leadR),
            new StereoStemBuffer("backing_vocals", backL, backR),
            new StereoStemBuffer("vocals", vocAllL, vocAllR),
            new StereoStemBuffer("instrumental", instL, instR)
        ]
    };

    static (float[] OutL, float[] OutR, int NonFinite, int Clips) RunMode(NeuralSeparatedTrack trk, float[] mL, float[] mR, bool preserveBacking) {
        using NeuralStemStageProcessor proc = new();
        proc.Configure(sr, SpatialProgramMetadata.CreateDefault());
        proc.BindKaraokeReferenceTrack(trk);
        proc.ApplyConfig(new NeuralAudioConfig {
            SpatialKaraokeEnabled = true,
            KaraokeVocalLevel = 0f,
            KaraokePreserveBackingVocals = preserveBacking
        });
        float[] oL = (float[])mL.Clone();
        float[] oR = (float[])mR.Clone();
        SpatialSourceDescriptor[] desc = [
            new() { Index = 0, Layer = SourceLayer.Bed, NormalizedPosition = new Vector3(-0.7f, 0f, 0.7f) },
            new() { Index = 1, Layer = SourceLayer.Bed, NormalizedPosition = new Vector3(0.7f, 0f, 0.7f) }
        ];
        for (int off = 0; off < mL.Length; off += 1024) {
            int blk = Math.Min(1024, mL.Length - off);
            float[][] slice = [new float[blk], new float[blk]];
            Array.Copy(oL, off, slice[0], 0, blk);
            Array.Copy(oR, off, slice[1], 0, blk);
            proc.Process(new SpatialSceneFrame {
                SampleRate = sr,
                FrameCount = blk,
                TrackSamplePosition = off,
                SourceBuffers = slice,
                ActiveSourceBufferCount = 2,
                Sources = desc
            });
            Array.Copy(slice[0], 0, oL, off, blk);
            Array.Copy(slice[1], 0, oR, off, blk);
        }
        int nonFin = 0, clips = 0;
        for (int i = 0; i < oL.Length; i++) {
            if (!float.IsFinite(oL[i]) || !float.IsFinite(oR[i])) nonFin++;
            if (MathF.Abs(oL[i]) > 1.0f || MathF.Abs(oR[i]) > 1.0f) clips++;
        }
        return (oL, oR, nonFin, clips);
    }

    static double MeasureToneDb(float[] buf, double freqHz, int startIdx) {
        double re = 0.0, im = 0.0;
        int n = buf.Length - startIdx;
        for (int i = startIdx; i < buf.Length; i++) {
            double ang = 2.0 * Math.PI * freqHz * i / sr;
            re += buf[i] * Math.Cos(ang);
            im += buf[i] * Math.Sin(ang);
        }
        double mag = 2.0 * Math.Sqrt(re * re + im * im) / n;
        return 20.0 * Math.Log10(Math.Max(1e-9, mag));
    }

    const int settle = 8192;
    double refLeadDb = MeasureToneDb(mixL, 440.0, settle);
    double refBackDb = MeasureToneDb(mixL, 660.0, settle);
    double refInstDb = MeasureToneDb(mixL, 185.0, settle);
    double refInstHiDb = MeasureToneDb(mixL, 1180.0, settle);

    var (keepL, _, nfKeep, clKeep) = RunMode(track, mixL, mixR, preserveBacking: true);
    var (pureL, _, nfPure, clPure) = RunMode(track, mixL, mixR, preserveBacking: false);

    Assert(nfKeep == 0 && clKeep == 0 && nfPure == 0 && clPure == 0, $"Non-finite or clips detected: keep=({nfKeep},{clKeep}) pure=({nfPure},{clPure})");

    double keepLeadAttenDb = refLeadDb - MeasureToneDb(keepL, 440.0, settle);
    double keepBackDeltaDb = Math.Abs(MeasureToneDb(keepL, 660.0, settle) - refBackDb);
    double keepInstDeltaDb = Math.Abs(MeasureToneDb(keepL, 185.0, settle) - refInstDb);

    double pureLeadAttenDb = refLeadDb - MeasureToneDb(pureL, 440.0, settle);
    double pureBackAttenDb = refBackDb - MeasureToneDb(pureL, 660.0, settle);
    double pureInstDeltaDb = Math.Abs(MeasureToneDb(pureL, 185.0, settle) - refInstDb);
    double pureInstHiDeltaDb = Math.Abs(MeasureToneDb(pureL, 1180.0, settle) - refInstHiDb);

    Assert(keepLeadAttenDb >= 20.0, $"KeepBacking must attenuate lead >= 20 dB, got {keepLeadAttenDb:0.2} dB");
    Assert(keepBackDeltaDb <= 1.5, $"KeepBacking must preserve backing <= 1.5 dB delta, got {keepBackDeltaDb:0.2} dB");
    Assert(keepInstDeltaDb <= 1.2, $"KeepBacking must preserve instruments <= 1.2 dB delta, got {keepInstDeltaDb:0.2} dB");

    Assert(pureLeadAttenDb >= 20.0, $"PureInstrumental must attenuate lead >= 20 dB, got {pureLeadAttenDb:0.2} dB");
    Assert(pureBackAttenDb >= 20.0, $"PureInstrumental must attenuate backing >= 20 dB, got {pureBackAttenDb:0.2} dB");
    Assert(pureInstDeltaDb <= 1.2 && pureInstHiDeltaDb <= 1.2,
        $"PureInstrumental damaged instruments: 185Hz={pureInstDeltaDb:0.2} dB, 1180Hz={pureInstHiDeltaDb:0.2} dB");

    return new Dictionary<string, object> {
        ["keepLeadAttenDb"] = Math.Round(keepLeadAttenDb, 2),
        ["keepBackingDeltaDb"] = Math.Round(keepBackDeltaDb, 2),
        ["keepInstDeltaDb"] = Math.Round(keepInstDeltaDb, 2),
        ["pureLeadAttenDb"] = Math.Round(pureLeadAttenDb, 2),
        ["pureBackingAttenDb"] = Math.Round(pureBackAttenDb, 2),
        ["pureInstLowDeltaDb"] = Math.Round(pureInstDeltaDb, 2),
        ["pureInstHiDeltaDb"] = Math.Round(pureInstHiDeltaDb, 2)
    };
});

// =========================================================================
// GT-12: Omniscient Cortex ComponentBehaviorProfile (<= 1.0 dB unseen RMS error) & Conflict Governor
// =========================================================================
RunSuite("GT-12", "ComponentBehaviorProfile_Accuracy_ConflictGovernance_And_Headroom", () => {
    const int sr = 48000;
    Stopwatch buildSw = Stopwatch.StartNew();
    ComponentBehaviorRegistry reg = ComponentBehaviorProfiler.BuildCavernDefaultRegistry(sr);
    buildSw.Stop();

    Assert(reg.Count >= 6, $"Expected >=6 auto-profiled Cavern components, got {reg.Count}");
    string manifestJson = reg.ToCompactManifestJson();
    Assert(manifestJson.Contains(ComponentBehaviorProfiler.IdTruePeakLimiter, StringComparison.Ordinal) &&
           manifestJson.Contains(ComponentBehaviorProfiler.IdLiveVocalChain, StringComparison.Ordinal),
        "Registry compact JSON manifest missing expected component IDs.");

    // Helper: measure actual steady-state output RMS on an unseen 1 kHz probe level
    static float MeasureActualRmsDb(Action<float[]> proc, float targetRmsDb) {
        int toneLen = 48000 / 2; // 500 ms
        int settle = (int)(toneLen * 0.65f);
        float amp = MathF.Pow(10f, targetRmsDb / 20f) * MathF.Sqrt(2f);
        float[] buf = new float[toneLen];
        double w = 2.0 * Math.PI * 1000.0 / 48000.0;
        for (int i = 0; i < toneLen; i++) {
            buf[i] = amp * (float)Math.Sin(w * i);
        }
        proc(buf);
        double sq = 0.0;
        int n = toneLen - settle;
        for (int i = settle; i < toneLen; i++) {
            sq += buf[i] * buf[i];
        }
        double rms = Math.Sqrt(sq / n);
        return rms > 1e-7 ? (float)(20.0 * Math.Log10(rms)) : -140f;
    }

    (string Id, Func<Action<float[]>> Factory)[] targets = [
        (ComponentBehaviorProfiler.IdTruePeakLimiter, () => {
            TruePeakLimiter lim = new(sr, 1, -1.0f, 80f);
            return b => lim.ProcessInterleaved(b, 0, b.Length, 1);
        }),
        (ComponentBehaviorProfiler.IdIso226Loudness, () => {
            Iso226LoudnessFilter iso = new(sr, 1);
            iso.SetParameters(true, 85f, 68f, false);
            return b => iso.ProcessInterleaved(b, 0, b.Length, 1);
        }),
        (ComponentBehaviorProfiler.IdSpatialDialog, () => {
            SpatialDialogEnhancer dlg = new();
            dlg.Configure(sr);
            dlg.SetParameters(true, 3.0f);
            return b => {
                float[] st = new float[b.Length * 2];
                for (int i = 0; i < b.Length; i++) { st[i * 2] = st[i * 2 + 1] = b[i]; }
                dlg.ProcessStereoInterleaved(st, 0, b.Length);
                for (int i = 0; i < b.Length; i++) { b[i] = 0.5f * (st[i * 2] + st[i * 2 + 1]); }
            };
        }),
        (ComponentBehaviorProfiler.IdNormalizer, () => {
            Normalizer norm = new(true);
            return b => norm.Process(b);
        }),
        (ComponentBehaviorProfiler.IdLiveVocalChain, () => {
            LiveVocalChainDsp v = new(sr);
            v.Configure(sr, VocalMixDnaProfile.CreateStudioDefault(), new VocalMacroSettings {
                Enabled = true, Bypass = false, MixMatchPct = 100f, DynamicsPct = 100f, WarmthPct = 45f,
                ReverbPct = 0f, EnableNoiseGate = false, EnableDeEsser = true, EnableSidechainPocketGlue = false
            });
            return b => {
                float[] st = new float[b.Length * 2];
                for (int i = 0; i < b.Length; i++) { st[i * 2] = st[i * 2 + 1] = b[i]; }
                v.ProcessStereoBlockZeroGc(st, 0, b.Length);
                for (int i = 0; i < b.Length; i++) { b[i] = 0.5f * (st[i * 2] + st[i * 2 + 1]); }
            };
        }),
        (ComponentBehaviorProfiler.IdAirHighShelf, () => {
            HighShelf shelf = new(sr, 8500.0, 0.71, 2.5);
            return b => shelf.Process(b);
        })
    ];

    float[] unseenLevels = [-27.0f, -15.0f, -9.0f];
    float maxPredErrDb = 0f;
    string worstComponent = "";
    foreach (var (id, factory) in targets) {
        Assert(reg.TryGet(id, out ComponentBehaviorProfile prof), $"Profile missing: {id}");
        foreach (float lv in unseenLevels) {
            float predDb = prof.PredictOutputRmsDb(lv);
            float actualDb = MeasureActualRmsDb(factory(), lv);
            float errDb = MathF.Abs(predDb - actualDb);
            if (errDb > maxPredErrDb) {
                maxPredErrDb = errDb;
                worstComponent = $"{id}@{lv:0}dB";
            }
            Assert(errDb <= 1.0f, $"Profile RMS error > 1.0 dB on {id} @ {lv:0.0} dBFS: pred={predDb:0.2}, actual={actualDb:0.2}, err={errDb:0.2} dB");
        }
    }

    // Verify ComponentConflictGovernor detects hardware Scarlett Air+Drive, measured BRIR, stacked makeup, and enforces <= -1.0 dBTP
    HardwarePreAmpStateSnapshot scarlett = new(
        airMode: ScarlettAirMode.PresenceAndDrive,
        driveActive: true,
        gainStepEpoch: 1,
        micSource: HardwarePreAmpStateSnapshot.MicSourceUsbInterface,
        isHardwareVerified: true);
    List<ComponentBehaviorProfile> chain = [];
    foreach (string cid in new[] {
        ComponentBehaviorProfiler.IdLiveVocalChain,
        ComponentBehaviorProfiler.IdSpatialDialog,
        ComponentBehaviorProfiler.IdAirHighShelf,
        ComponentBehaviorProfiler.IdTruePeakLimiter
    }) {
        Assert(reg.TryGet(cid, out ComponentBehaviorProfile p), $"Missing chain component {cid}");
        chain.Add(p);
    }
    ChainGovernancePlan plan = ComponentConflictGovernor.EvaluateChain(chain, -12.0f, 12.0f, scarlett, isMeasuredBrirActive: true);
    Assert(plan.SkipSoftwareAir && plan.SkipSoftwareDrive, "Governor must suppress redundant software Air & Drive when Scarlett PresenceAndDrive is active.");
    Assert(plan.SkipSyntheticRoom, "Governor must suppress synthetic room when measured BRIR is active.");
    Assert((plan.DetectedConflicts & ComponentConflictFlags.HighShelfAir) != 0 &&
           (plan.DetectedConflicts & ComponentConflictFlags.AnalogDrive) != 0 &&
           (plan.DetectedConflicts & ComponentConflictFlags.RoomEarlyReflections) != 0 &&
           (plan.DetectedConflicts & ComponentConflictFlags.MakeupGain) != 0,
        $"Expected Air|Drive|Room|Makeup conflicts, got {plan.DetectedConflicts}");
    Assert(plan.IsTruePeakSafe && plan.PredictedPeakDbTp <= -0.95f,
        $"Governed peak exceeded -1.0 dBTP ceiling: {plan.PredictedPeakDbTp:0.2} dBTP");

    return new Dictionary<string, object> {
        ["profiledComponents"] = reg.Count,
        ["buildMs"] = Math.Round(buildSw.Elapsed.TotalMilliseconds, 1),
        ["maxUnseenRmsErrDb"] = Math.Round(maxPredErrDb, 3),
        ["worstPoint"] = worstComponent,
        ["governedPeakDbTp"] = Math.Round(plan.PredictedPeakDbTp, 2),
        ["conflictsDetected"] = (int)plan.DetectedConflicts
    };
});

// =========================================================================
// GT-13: Phase 3 Slot 2D-SHIELD (Multi-Ref FDKF + Shadow Filter + AcousticConstraintGovernor)
// =========================================================================
RunSuite("GT-13", "Slot2DShield_MultiRefFDKF_ShadowFilter_And_AcousticConstraintGovernor", () => {
    const int sr = 48000;
    const int blk = MultiRefFdkfShield.DefaultBlockSize; // 128
    const int rirLen = 96;

    // Deterministic synthetic acoustic room impulse responses (speaker L -> mic, speaker R -> mic, voice speaker -> mic)
    float[] hL = new float[rirLen];
    float[] hR = new float[rirLen];
    float[] hV = new float[rirLen];
    hL[12] = 0.42f; hL[29] = -0.21f; hL[51] = 0.12f; hL[77] = -0.06f;
    hR[16] = 0.38f; hR[34] = 0.19f;  hR[59] = -0.11f; hR[83] = 0.05f;
    hV[10] = 0.48f; hV[24] = 0.22f;  hV[43] = -0.10f; // Peak |H_v(w)| ~ 0.65-0.75

    // Deterministic Xorshift32 broadband stereo excitation (uncorrelated L & R music)
    uint rng = 0x13579BDFu;
    float NextNoise() {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return ((rng & 0xFFFF) / 32768f - 1f) * 0.25f;
    }

    int totalBlocks = 420;
    int totalSamples = totalBlocks * blk;
    float[] musicL = new float[totalSamples];
    float[] musicR = new float[totalSamples];
    float[] voiceRef = new float[totalSamples];
    for (int i = 0; i < totalSamples; i++) {
        double t = i / (double)sr;
        musicL[i] = NextNoise() + 0.08f * MathF.Sin((float)(2 * Math.PI * 220.0 * t));
        musicR[i] = NextNoise() + 0.08f * MathF.Cos((float)(2 * Math.PI * 310.0 * t));
        // Voice feedback reference active in blocks 260..360 (simulating speaker feedback path identification)
        if (i >= 260 * blk && i < 360 * blk) {
            voiceRef[i] = NextNoise() * 0.85f;
        }
    }

    static float ConvolveAt(float[] sig, float[] rir, int idx) {
        float acc = 0f;
        int lim = Math.Min(rir.Length, idx + 1);
        for (int k = 0; k < lim; k++) {
            float h = rir[k];
            if (h != 0f) {
                acc += sig[idx - k] * h;
            }
        }
        return acc;
    }

    MultiRefFdkfShield shield = new(blk, partitions: 4, referenceCount: 3);
    AcousticConstraintGovernor governor = new(safetyMarginDb: 6.0f, headphoneMaxMakeupDb: 12.0f);
    OmniscientVocalStateBus stateBus = new();
    HardwarePreAmpStateSnapshot speakerPreAmp = new(
        gainStepEpoch: 1,
        isHeadphoneMonitoring: false,
        micSource: HardwarePreAmpStateSnapshot.MicSourceUsbInterface,
        isHardwareVerified: true);

    float[] micBlk = new float[blk];
    float[] refLBlk = new float[blk];
    float[] refRBlk = new float[blk];
    float[] refVBlk = new float[blk];
    float[] cleanVocalBlk = new float[blk];

    double p1MicSq = 0, p1ErrSq = 0;
    double p2CleanSq = 0, p2OutSq = 0;
    double p3MicSq = 0, p3ErrSq = 0;
    int nonFinite = 0;

    // Phase 1 (blocks 0..139): Stereo music bleed convergence (no singing yet)
    for (int b = 0; b < 140; b++) {
        int off = b * blk;
        for (int i = 0; i < blk; i++) {
            int idx = off + i;
            refLBlk[i] = musicL[idx];
            refRBlk[i] = musicR[idx];
            refVBlk[i] = 0f;
            micBlk[i] = ConvolveAt(musicL, hL, idx) + ConvolveAt(musicR, hR, idx);
        }
        double micPow = 0;
        for (int i = 0; i < blk; i++) { micPow += micBlk[i] * micBlk[i]; }

        shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, speakerPreAmp, governor, stateBus);

        double errPow = 0;
        for (int i = 0; i < blk; i++) {
            if (!float.IsFinite(micBlk[i])) nonFinite++;
            errPow += micBlk[i] * micBlk[i];
        }
        if (b >= 100) {
            p1MicSq += micPow;
            p1ErrSq += errPow;
        }
    }
    double p1ErleDb = 10.0 * Math.Log10(Math.Max(1e-12, p1MicSq) / Math.Max(1e-12, p1ErrSq));
    Assert(nonFinite == 0, $"Non-finite samples in GT-13 Phase 1: {nonFinite}");
    Assert(p1ErleDb >= 25.0, $"GT-13 Phase 1 Music-Bleed ERLE must be >= 25.0 dB, got {p1ErleDb:0.2} dB");
    Assert(shield.ShadowCommitCount > 0, "Expected Two-Path Shadow Filter commits > 0.");

    // Phase 2 (blocks 140..179): Simultaneous singing double-talk preservation
    for (int b = 140; b < 180; b++) {
        int off = b * blk;
        for (int i = 0; i < blk; i++) {
            int idx = off + i;
            double t = idx / (double)sr;
            float vocal = 0.30f * MathF.Sin((float)(2 * Math.PI * 440.0 * t)) +
                          0.12f * MathF.Sin((float)(2 * Math.PI * 880.0 * t)) +
                          0.06f * MathF.Sin((float)(2 * Math.PI * 1320.0 * t));
            cleanVocalBlk[i] = vocal;
            refLBlk[i] = musicL[idx];
            refRBlk[i] = musicR[idx];
            refVBlk[i] = 0f;
            micBlk[i] = vocal + ConvolveAt(musicL, hL, idx) + ConvolveAt(musicR, hR, idx);
        }
        shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, speakerPreAmp, governor, stateBus);
        for (int i = 0; i < blk; i++) {
            if (!float.IsFinite(micBlk[i])) nonFinite++;
            p2CleanSq += cleanVocalBlk[i] * cleanVocalBlk[i];
            p2OutSq += micBlk[i] * micBlk[i];
        }
    }
    double vocalRetentionDb = 10.0 * Math.Log10(Math.Max(1e-12, p2OutSq) / Math.Max(1e-12, p2CleanSq));
    Assert(Math.Abs(vocalRetentionDb) <= 1.5, $"Double-talk vocal energy must stay within +/-1.5 dB of clean vocal, got {vocalRetentionDb:0.2} dB");

    // Phase 3 (blocks 180..259): Scarlett preamp GainStepEpoch jump (+4.1 dB room scale) -> rapid reconvergence
    HardwarePreAmpStateSnapshot steppedPreAmp = new(
        gainStepEpoch: 2,
        isHeadphoneMonitoring: false,
        micSource: HardwarePreAmpStateSnapshot.MicSourceUsbInterface,
        isHardwareVerified: true);
    for (int b = 180; b < 260; b++) {
        int off = b * blk;
        for (int i = 0; i < blk; i++) {
            int idx = off + i;
            refLBlk[i] = musicL[idx];
            refRBlk[i] = musicR[idx];
            refVBlk[i] = 0f;
            micBlk[i] = 1.6f * (ConvolveAt(musicL, hL, idx) + ConvolveAt(musicR, hR, idx));
        }
        double micPow = 0;
        for (int i = 0; i < blk; i++) { micPow += micBlk[i] * micBlk[i]; }
        shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, steppedPreAmp, governor, stateBus);
        double errPow = 0;
        for (int i = 0; i < blk; i++) { errPow += micBlk[i] * micBlk[i]; }
        if (b >= 230) {
            p3MicSq += micPow;
            p3ErrSq += errPow;
        }
    }
    double p3ErleDb = 10.0 * Math.Log10(Math.Max(1e-12, p3MicSq) / Math.Max(1e-12, p3ErrSq));
    Assert(shield.LastGainStepEpoch == 2, $"Expected shield LastGainStepEpoch==2, got {shield.LastGainStepEpoch}");
    Assert(p3ErleDb >= 22.0, $"Post-GainStepEpoch reconvergence ERLE must be >= 22.0 dB, got {p3ErleDb:0.2} dB");

    // Phase 4 (blocks 260..359): Voice feedback path identification & AcousticConstraintGovernor GBF stability
    for (int b = 260; b < 360; b++) {
        int off = b * blk;
        for (int i = 0; i < blk; i++) {
            int idx = off + i;
            refLBlk[i] = 0f;
            refRBlk[i] = 0f;
            refVBlk[i] = voiceRef[idx];
            micBlk[i] = ConvolveAt(voiceRef, hV, idx);
        }
        shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, steppedPreAmp, governor, stateBus);
    }
    float[] identifiedCoupling = new float[AcousticConstraintGovernor.SubBandCount];
    shield.CopyIdentifiedCouplingZeroGc(identifiedCoupling);
    float peakIdentifiedH = governor.PeakRoomCouplingLinear;
    Assert(peakIdentifiedH >= 0.45f, $"Expected identified voice feedback coupling |H_room| >= 0.45, got {peakIdentifiedH:0.3}");

    // Naive ungoverned +12 dB vocal makeup gain vs AcousticConstraintGovernor
    float requestedMakeupDb = 12.0f;
    float governedMakeupDb;
    governor.EvaluateZeroGc(identifiedCoupling, requestedMakeupDb, isHeadphoneMonitoring: false, out governedMakeupDb);
    float ungovernedLoopGainDb = 20f * MathF.Log10(peakIdentifiedH) + requestedMakeupDb;
    float governedLoopGainDb = 20f * MathF.Log10(peakIdentifiedH) + governedMakeupDb;
    float gbfImprovementDb = ungovernedLoopGainDb - governedLoopGainDb;
    Assert(ungovernedLoopGainDb > 0f, $"Test setup sanity: ungoverned +12 dB loop must exceed 0 dB, got {ungovernedLoopGainDb:0.2} dB");
    Assert(governor.CurrentLoopMarginDb >= 5.5f && governedLoopGainDb <= -5.5f,
        $"Governor must maintain >= 5.5 dB stability margin, got margin={governor.CurrentLoopMarginDb:0.2} dB, loop={governedLoopGainDb:0.2} dB");
    Assert(gbfImprovementDb >= 8.0f, $"Expected GBF stability improvement >= +8.0 dB over ungoverned chain, got +{gbfImprovementDb:0.2} dB");

    // Verify headphone monitoring auto-bypass (bit-exact passthrough & studio +12 dB headroom restored)
    HardwarePreAmpStateSnapshot hpPreAmp = new(
        gainStepEpoch: 2,
        isHeadphoneMonitoring: true,
        micSource: HardwarePreAmpStateSnapshot.MicSourceUsbInterface,
        isHardwareVerified: true);
    float[] hpExpected = new float[blk];
    for (int i = 0; i < blk; i++) { micBlk[i] = hpExpected[i] = 0.17f * MathF.Sin(i * 0.13f); }
    shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, hpPreAmp, governor, stateBus);
    Assert(shield.IsBypassed, "Headphone monitoring must automatically bypass speaker-bleed shield.");
    float hpMaxDiff = 0f;
    for (int i = 0; i < blk; i++) { hpMaxDiff = MathF.Max(hpMaxDiff, MathF.Abs(micBlk[i] - hpExpected[i])); }
    Assert(hpMaxDiff == 0f, $"Headphone bypass must be bit-exact passthrough, got maxDiff={hpMaxDiff}");
    OmniscientVocalStateSnapshot busSnap = stateBus.ReadSnapshot();
    Assert(busSnap.IsHeadphoneMonitoring && busSnap.IsShieldBypassed && !busSnap.PhaseBActive && busSnap.GovernorMaxMakeupDb >= 11.9f,
        "StateBus snapshot mismatch after headphone bypass.");

    // Phase 5 (blocks 360..419): Hard Real-Time 0 B GC & < 1.0 ms/block latency budget verification
    // Clear prior suites' garbage & warm up 4 blocks in speaker mode before measuring allocation & median latency
    GC.Collect();
    GC.WaitForPendingFinalizers();
    for (int w = 0; w < 4; w++) {
        shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, steppedPreAmp, governor, stateBus);
    }
    const int rtBlocks = 60;
    double[] blockMsSamples = new double[rtBlocks];
    double ticksToMs = 1000.0 / Stopwatch.Frequency;
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int b = 0; b < rtBlocks; b++) {
        long t0 = Stopwatch.GetTimestamp();
        shield.ProcessBlockZeroGc(micBlk, 0, refLBlk, 0, refRBlk, 0, refVBlk, 0, steppedPreAmp, governor, stateBus);
        blockMsSamples[b] = (Stopwatch.GetTimestamp() - t0) * ticksToMs;
    }
    long allocAfter = GC.GetAllocatedBytesForCurrentThread();
    long rtAllocBytes = allocAfter - allocBefore;
    Array.Sort(blockMsSamples);
    double medianBlockMs = blockMsSamples[rtBlocks / 2];
    Assert(rtAllocBytes == 0, $"MultiRefFdkfShield hot path must allocate 0 B GC, allocated {rtAllocBytes} B over {rtBlocks} blocks");
    Assert(medianBlockMs < 1.0, $"MultiRefFdkfShield must run < 1.0 ms/block (median), measured {medianBlockMs:0.3} ms/block");

    return new Dictionary<string, object> {
        ["musicBleedErleDb"] = Math.Round(p1ErleDb, 2),
        ["doubleTalkVocalDeltaDb"] = Math.Round(vocalRetentionDb, 2),
        ["postGainStepErleDb"] = Math.Round(p3ErleDb, 2),
        ["peakIdentifiedH"] = Math.Round(peakIdentifiedH, 3),
        ["gbfImprovementDb"] = Math.Round(gbfImprovementDb, 2),
        ["governorMarginDb"] = Math.Round(governor.CurrentLoopMarginDb, 2),
        ["shadowCommits"] = shield.ShadowCommitCount,
        ["rtAllocBytes"] = rtAllocBytes,
        ["rtMedianBlockMs"] = Math.Round(medianBlockMs, 4)
    };
});

// =========================================================================
// GT-14: W53.1 B2 Low-Band Vocal Rumble Fix (Under +12 dB Bass) + B3 FLAC<->Atmos Cross-Variant GCC-PHAT Karaoke
// =========================================================================
RunSuite("GT-14", "W53_1_LowBandVocalRumbleFix_And_FlacAtmosCrossVariantGccPhatKaraoke", () => {
    const int sr = 48000;
    const int nA = sr * 3; // 3.0 s stereo + LFE low-band bass-masked vocal test
    float[] cleanVocal = new float[nA];
    float[] vocalWithSubBleed = new float[nA];
    float[] backingBleed = new float[nA];
    float[] bassL = new float[nA];
    float[] bassR = new float[nA];
    float[] lfe = new float[nA];
    float[] mixL = new float[nA];
    float[] mixR = new float[nA];

    for (int i = 0; i < nA; i++) {
        double t = i / (double)sr;
        // Realistic vocal: low chest fundamentals (105, 155, 225 Hz) + mid formants (520, 980, 1420 Hz)
        float vLow = 0.055f * MathF.Sin((float)(2 * Math.PI * 105.0 * t))
                   + 0.050f * MathF.Sin((float)(2 * Math.PI * 155.0 * t + 0.4))
                   + 0.045f * MathF.Sin((float)(2 * Math.PI * 225.0 * t + 1.1));
        float vMid = 0.060f * MathF.Sin((float)(2 * Math.PI * 520.0 * t + 0.2))
                   + 0.055f * MathF.Sin((float)(2 * Math.PI * 980.0 * t + 0.9))
                   + 0.040f * MathF.Sin((float)(2 * Math.PI * 1420.0 * t + 1.7));
        float v = vLow + vMid;
        cleanVocal[i] = v;
        // Simulate MVSEP sub-50 Hz kick bleed (36 Hz) inside neural vocal stem
        vocalWithSubBleed[i] = v + 0.08f * MathF.Sin((float)(2 * Math.PI * 36.0 * t));
        // Simulate -10 dB lead bleed inside backing stem (tests 2nd-order Wiener leadPurity)
        backingBleed[i] = 0.316f * v;

        // Loud uncorrelated bass (+12 dB hotter than low vocal fundamentals, sharing 46.875 Hz STFT bins!)
        float b1 = 0.28f * MathF.Sin((float)(2 * Math.PI * 58.0 * t + 0.7));
        float b2 = 0.24f * MathF.Sin((float)(2 * Math.PI * 112.0 * t + 1.9));
        float b3 = 0.20f * MathF.Sin((float)(2 * Math.PI * 172.0 * t + 2.6));
        bassL[i] = b1 + b2 + b3;
        bassR[i] = b1 - 0.85f * b2 + 0.90f * b3;
        lfe[i] = 0.40f * MathF.Sin((float)(2 * Math.PI * 42.0 * t));

        mixL[i] = v + bassL[i];
        mixR[i] = v + bassR[i];
    }

    // Apply zero-phase sub-50 Hz HP to vocal reference just as MvsepCloudClient does
    float[] filteredVocalRef = (float[])vocalWithSubBleed.Clone();
    MvsepCloudClient.ApplyZeroPhaseSubVocalHighPass(filteredVocalRef, sr, 50f);

    // Run real-time SpatialKaraokeEngine in 512-sample blocks on 3 channels (FL, FR, LFE)
    using SpatialKaraokeEngine rtKaraoke = new(sr, 3) {
        IsEnabled = true,
        VocalLevel = 0.0f,
        PreserveBackingVocals = true
    };
    float[][] rtCh = [(float[])mixL.Clone(), (float[])mixR.Clone(), (float[])lfe.Clone()];
    bool[] isLfe3 = [false, false, true];
    bool[] isFront3 = [true, true, false];
    const int blk = 512;
    float[][] blkCh = [new float[blk], new float[blk], new float[blk]];
    float[] blkVoc = new float[blk];
    float[] blkBack = new float[blk];
    long rtAllocBytes = 0;

    // Warm up first 10 blocks, then measure RT GC allocations over 60 steady-state blocks
    for (int off = 0, bIdx = 0; off + blk <= nA; off += blk, bIdx++) {
        Array.Copy(rtCh[0], off, blkCh[0], 0, blk);
        Array.Copy(rtCh[1], off, blkCh[1], 0, blk);
        Array.Copy(rtCh[2], off, blkCh[2], 0, blk);
        Array.Copy(filteredVocalRef, off, blkVoc, 0, blk);
        Array.Copy(backingBleed, off, blkBack, 0, blk);

        if (bIdx == 20) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        long a0 = bIdx >= 20 && bIdx < 80 ? GC.GetAllocatedBytesForCurrentThread() : 0;
        rtKaraoke.ProcessMultichannelInPlace(blkCh, isLfe3, isFront3, 3, blk, blkVoc, blkBack);
        if (bIdx >= 20 && bIdx < 80) {
            rtAllocBytes += GC.GetAllocatedBytesForCurrentThread() - a0;
        }

        Array.Copy(blkCh[0], 0, rtCh[0], off, blk);
        Array.Copy(blkCh[1], 0, rtCh[1], off, blk);
        Array.Copy(blkCh[2], 0, rtCh[2], off, blk);
    }

    // Account for exact 512-sample COLA hop latency in streaming mode when comparing steady-state [0.7s..2.7s]
    int steadyStart = (int)(sr * 0.7);
    int steadyLen = (int)(sr * 2.0);
    double vocalLowOrigPow = 0, vocalLowResPow = 0, bassOrigPow = 0, bassOutPow = 0;
    double[] lowVocFreqs = [105.0, 155.0, 225.0];
    double[] bassFreqs = [58.0, 112.0, 172.0];
    foreach (double f in lowVocFreqs) {
        vocalLowOrigPow += GoertzelPower(mixL, steadyStart, steadyLen, sr, f);
        vocalLowResPow += GoertzelPower(rtCh[0], steadyStart + SpatialKaraokeEngine.HopSize, steadyLen, sr, f);
    }
    foreach (double f in bassFreqs) {
        bassOrigPow += GoertzelPower(bassL, steadyStart, steadyLen, sr, f);
        bassOutPow += GoertzelPower(rtCh[0], steadyStart + SpatialKaraokeEngine.HopSize, steadyLen, sr, f);
    }
    double lowBandResidualDb = 10.0 * Math.Log10(Math.Max(1e-12, vocalLowResPow) / Math.Max(1e-12, vocalLowOrigPow));
    double bassDeltaDb = 10.0 * Math.Log10(Math.Max(1e-12, bassOutPow) / Math.Max(1e-12, bassOrigPow));
    float lfeMaxDiff = 0f;
    for (int i = 0; i < nA; i++) {
        lfeMaxDiff = MathF.Max(lfeMaxDiff, MathF.Abs(rtCh[2][i] - lfe[i]));
    }
    Assert(lfeMaxDiff == 0f, $"GT-14 LFE must remain bit-exact (0.00 dB), got maxDiff={lfeMaxDiff}");
    Assert(rtAllocBytes == 0, $"GT-14 SpatialKaraokeEngine RT path must allocate 0 B GC, allocated {rtAllocBytes} B");
    Assert(lowBandResidualDb <= -24.0, $"GT-14 B2 low-band vocal residual under +12 dB bass must be <= -24.0 dB, got {lowBandResidualDb:F2} dB");
    Assert(Math.Abs(bassDeltaDb) <= 1.0, $"GT-14 B2 uncorrelated bass energy delta must be within +/-1.0 dB, got {bassDeltaDb:F2} dB");

    // Part B: B3 FLAC <-> Atmos separate-reference karaoke with +187.35 ms offset & +25.0 ppm clock drift
    const int nB = sr * 6; // 6.0 s so sliding GCC-PHAT Theil-Sen estimates drift cleanly
    const double trueOffsetSamples = 0.18735 * sr; // +8992.8 samples
    const double trueDriftPpm = 25.0;
    const double driftScale = 1.0 + trueDriftPpm * 1e-6;
    int flacLen = nB + 16000;

    // Deterministic rich musical signal (non-periodic drum/chord onsets + band-limited musical texture + chest/mid vocals)
    Random rng = new(53101);
    float[] masterVoc = new float[flacLen];
    float[] masterInstL = new float[flacLen];
    float[] masterInstR = new float[flacLen];
    double[] hitTimesSec = [0.25, 0.72, 1.08, 1.63, 2.05, 2.48, 3.12, 3.55, 3.91, 4.47, 4.98, 5.42];
    int leadIn = (int)Math.Floor(trueOffsetSamples);
    float lpCenter = 0f, lpSide = 0f;
    for (int i = 0; i < flacLen; i++) {
        // Real FLAC vs Atmos lead-in offset: FLAC starts after +187.35 ms (+8992.8 samples) of silence
        if (i < leadIn - 16) {
            continue;
        }
        double t = (i - trueOffsetSamples) / (double)sr;
        if (t < 0.0) {
            continue;
        }
        float env = 0.65f + 0.35f * MathF.Sin((float)(2 * Math.PI * 2.3 * t));
        masterVoc[i] = env * (
            0.09f * MathF.Sin((float)(2 * Math.PI * 125.0 * t)) +
            0.08f * MathF.Sin((float)(2 * Math.PI * 215.0 * t + 0.5)) +
            0.10f * MathF.Sin((float)(2 * Math.PI * 640.0 * t + 1.1)) +
            0.08f * MathF.Sin((float)(2 * Math.PI * 1350.0 * t + 1.8)));
        float hitEnv = 0.32f;
        for (int h = 0; h < hitTimesSec.Length; h++) {
            double dt = t - hitTimesSec[h];
            if (dt >= 0.0 && dt < 0.18) {
                hitEnv += 0.90f * MathF.Exp((float)(-dt / 0.045));
            }
        }
        lpCenter = 0.65f * lpCenter + 0.35f * (float)(rng.NextDouble() * 2.0 - 1.0);
        lpSide = 0.65f * lpSide + 0.35f * (float)(rng.NextDouble() * 2.0 - 1.0);
        float bass = 0.15f * MathF.Sin((float)(2 * Math.PI * 82.0 * t));
        masterInstL[i] = 0.30f * hitEnv * lpCenter + 0.10f * hitEnv * lpSide + bass;
        masterInstR[i] = 0.28f * hitEnv * lpCenter - 0.10f * hitEnv * lpSide + bass;
    }

    // Build Atmos timeline sampled from FLAC master via: flacIndex = trueOffsetSamples + atmosIndex * (1 + trueDriftPpm * 1e-6)
    float[] atmosFL = new float[nB];
    float[] atmosFR = new float[nB];
    float[] atmosFC = new float[nB];
    float[] atmosLFE = new float[nB];
    float[] atmosMono = new float[nB];
    for (int i = 0; i < nB; i++) {
        double pos = trueOffsetSamples + i * driftScale;
        float vAt = FarrowLagrangeDelay.SampleAt(masterVoc, flacLen, pos);
        float insLAt = FarrowLagrangeDelay.SampleAt(masterInstL, flacLen, pos);
        float insRAt = FarrowLagrangeDelay.SampleAt(masterInstR, flacLen, pos);
        atmosFC[i] = vAt;
        atmosFL[i] = 0.35f * vAt + insLAt;
        atmosFR[i] = 0.35f * vAt + insRAt;
        atmosLFE[i] = 0.30f * MathF.Sin((float)(2 * Math.PI * 40.0 * i / sr));
        atmosMono[i] = 0.5f * (atmosFL[i] + atmosFR[i]) + 0.5f * atmosFC[i];
    }

    NeuralSeparatedTrack flacStems = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
        "song_flac_variant.flac",
        sr,
        [
            new StereoStemBuffer("vocals", masterVoc, masterVoc),
            new StereoStemBuffer("instrumental", masterInstL, masterInstR)
        ],
        dereverb: null,
        derivedFromLosslessTwin: false,
        planSignature: "mvsep_test_flac");

    CrossVariantAlignmentResult align = CrossVariantVocalAligner.Align(atmosMono, sr, flacStems);
    Assert(align.IsAligned && !align.ShouldPreferSeparateExtraction,
        $"GT-14 B3 CrossVariantVocalAligner failed to align FLAC<->Atmos (conf={align.Confidence:F3}, probe={align.ProbeSource}, off={align.OffsetSamples:F2}, drift={align.DriftPpm:F2})");
    Assert(Math.Abs(align.OffsetSamples - trueOffsetSamples) <= 1.2,
        $"GT-14 B3 offset error too high: expected {trueOffsetSamples:F2}, got {align.OffsetSamples:F2}");
    Assert(Math.Abs(align.DriftPpm - trueDriftPpm) <= 10.0,
        $"GT-14 B3 drift error too high: expected {trueDriftPpm:F1} ppm, got {align.DriftPpm:F1} ppm");

    // Verify NeuralStemStageProcessor applies both ReferenceSampleOffset & ReferenceDriftPpm seamlessly in real time
    NeuralStemStageProcessor stage = new();
    stage.SetSourceTrackSampleRate(sr);
    stage.ApplyConfig(new NeuralAudioConfig {
        SpatialKaraokeEnabled = true,
        KaraokeVocalLevel = 0.0f,
        KaraokePreserveBackingVocals = false
    });
    stage.BindKaraokeReferenceTrack(flacStems);
    stage.ReferenceSampleOffset = align.OffsetSamples;
    stage.ReferenceDriftPpm = align.DriftPpm;

    float[][] atmosBus = [(float[])atmosFL.Clone(), (float[])atmosFR.Clone(), (float[])atmosFC.Clone(), (float[])atmosLFE.Clone()];
    VirtualChannel[] chLayout = [
        new VirtualChannel { y = -30f, x = 0f, isLfe = false },
        new VirtualChannel { y = 30f, x = 0f, isLfe = false },
        new VirtualChannel { y = 0f, x = 0f, isLfe = false },
        new VirtualChannel { y = 0f, x = 0f, isLfe = true }
    ];
    float[][] vBlk = [new float[blk], new float[blk], new float[blk], new float[blk]];
    for (int off = 0; off + blk <= nB; off += blk) {
        for (int c = 0; c < 4; c++) {
            Array.Copy(atmosBus[c], off, vBlk[c], 0, blk);
        }
        stage.ProcessPreHrtfBus(vBlk, chLayout, 4, blk, sr);
        for (int c = 0; c < 4; c++) {
            Array.Copy(vBlk[c], 0, atmosBus[c], off, blk);
        }
    }

    int lateStart = sr * 3; // evaluate late in track (3s..5.5s) where uncompensated +25 ppm drift would cause severe phase drift!
    int lateLen = (int)(sr * 2.2);
    double atmosFcOrig = 0, atmosFcOut = 0;
    foreach (double f in new[] { 125.0, 215.0, 640.0, 1350.0 }) {
        atmosFcOrig += GoertzelPower(atmosFC, lateStart, lateLen, sr, f);
        atmosFcOut += GoertzelPower(atmosBus[2], lateStart + SpatialKaraokeEngine.HopSize, lateLen, sr, f);
    }
    double crossVariantResDb = 10.0 * Math.Log10(Math.Max(1e-12, atmosFcOut) / Math.Max(1e-12, atmosFcOrig));
    Assert(crossVariantResDb <= -24.0,
        $"GT-14 B3 cross-variant FLAC<->Atmos karaoke residual (late 3..5.5s w/ +187.35ms +25ppm) must be <= -24.0 dB, got {crossVariantResDb:F2} dB");

    // Verify incompatible master rejection (genuinely unrelated noise/edit -> IsAligned == false)
    float[] unrelated = new float[sr * 4];
    Random rngBad = new(99991);
    for (int i = 0; i < unrelated.Length; i++) {
        unrelated[i] = 0.25f * (float)(rngBad.NextDouble() * 2.0 - 1.0);
    }
    CrossVariantAlignmentResult rejected = CrossVariantVocalAligner.Align(unrelated, sr, flacStems);
    Assert(!rejected.IsAligned && rejected.ShouldPreferSeparateExtraction,
        $"GT-14 B3 incompatible master must be rejected cleanly, got IsAligned={rejected.IsAligned}, conf={rejected.Confidence:F3}");

    return new Dictionary<string, object> {
        ["lowBandVocalResidualDb"] = Math.Round(lowBandResidualDb, 2),
        ["bassEnergyDeltaDb"] = Math.Round(bassDeltaDb, 2),
        ["lfeMaxDiff"] = lfeMaxDiff,
        ["rtAllocBytes"] = rtAllocBytes,
        ["crossVariantOffsetErrSmp"] = Math.Round(Math.Abs(align.OffsetSamples - trueOffsetSamples), 3),
        ["crossVariantDriftPpm"] = Math.Round(align.DriftPpm, 2),
        ["crossVariantConf"] = Math.Round(align.Confidence, 3),
        ["crossVariantKaraokeResDb"] = Math.Round(crossVariantResDb, 2),
        ["incompatibleMasterRejected"] = !rejected.IsAligned
    };
});

// GT-15: W53.3 Wave C — Stereo-Pan Azimuth Preservation (QAS|Q4 pan_err_deg <= 5.0) & Mood-Aware 3D Choreography (0 B RT GC)
RunSuite("GT-15", "W53_3_WaveC_StereoPanPreservation_And_MoodAwareChoreo_QAS_Q4", () => {
    int sr = 48000;
    int len = sr * 4; // 4.0 seconds
    float[] synthSig = new float[len];
    float[] reverbTailL = new float[len];
    float[] reverbTailR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        double env = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * 0.5 * t);
        synthSig[i] = (float)(env * (0.35 * Math.Sin(2.0 * Math.PI * 440.0 * t)
                                   + 0.18 * Math.Sin(2.0 * Math.PI * 880.0 * t)
                                   + 0.10 * Math.Sin(2.0 * Math.PI * 1320.0 * t)));
        // Uncorrelated isotropic stereo diffuse reverb tails (E[reverbL * reverbR] == 0)
        reverbTailL[i] = 0.08f * (float)Math.Sin(2.0 * Math.PI * 1610.0 * t + 1.3);
        reverbTailR[i] = 0.08f * (float)Math.Sin(2.0 * Math.PI * 1970.0 * t + 2.7);
    }

    // 1. Test 5 stereo pan angles + diffuse stereo width on fixed 3D descriptors
    float[] testPans = [-52f, -28f, 0f, 24f, 48f];
    double maxPanErrDeg = 0.0;
    Vector3 roomEnv = new(2f, 2f, 2f);
    foreach (float targetAz in testPans) {
        double phi = ((targetAz / 60.0) + 1.0) * (Math.PI * 0.25);
        float gL = (float)Math.Cos(phi);
        float gR = (float)Math.Sin(phi);
        float[] sL = new float[len], sR = new float[len];
        for (int i = 0; i < len; i++) {
            sL[i] = synthSig[i] * gL + reverbTailL[i];
            sR[i] = synthSig[i] * gR + reverbTailR[i];
        }
        StereoStemBuffer stemBuf = new("guitar", sL, sR);
        StereoPanEstimate est = StereoPanAzimuthEstimator.Estimate(stemBuf);
        List<StereoStemBuffer> singleStem = [stemBuf];
        SpatialSourceDescriptor[] descs = NeuralStemSpatializer.BuildSceneDescriptors(singleStem, roomEnv);
        Assert(descs.Length == 2, $"Expected 2 descriptors for stereo guitar stem, got {descs.Length}");
        float azL = MathF.Atan2(descs[0].NormalizedPosition.X, descs[0].NormalizedPosition.Z) * (180f / MathF.PI);
        float azR = MathF.Atan2(descs[1].NormalizedPosition.X, descs[1].NormalizedPosition.Z) * (180f / MathF.PI);
        float centroidAz = 0.5f * (1f - est.PowerBalance) * azL + 0.5f * (1f + est.PowerBalance) * azR;
        double errDeg = Math.Abs(centroidAz - est.PanAzimuthDeg);
        if (errDeg > maxPanErrDeg) maxPanErrDeg = errDeg;
        double targetErrDeg = Math.Abs(est.PanAzimuthDeg - targetAz);
        Assert(targetErrDeg <= 2.5, $"GT-15 pan estimator drift on {targetAz}° w/ diffuse tail: est={est.PanAzimuthDeg:F2}°");
    }
    Assert(maxPanErrDeg <= 5.0, $"GT-15 QAS|Q4 fixed 3D pan_err_deg must be <= 5.0°, got {maxPanErrDeg:F2}°");

    // 2. Dynamic Choreography across Musical / Cinematic / Orbit with Mood Digests + Slew & Safety Bounds + 0 B RT GC
    float[] vocL = new float[len], vocR = new float[len];
    float[] basL = new float[len], basR = new float[len];
    float[] gtrL = new float[len], gtrR = new float[len];
    float[] pnoL = new float[len], pnoR = new float[len];
    double gPhi = ((-40.0 / 60.0) + 1.0) * (Math.PI * 0.25);
    double pPhi = ((35.0 / 60.0) + 1.0) * (Math.PI * 0.25);
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        vocL[i] = 0.35f * (float)Math.Sin(2.0 * Math.PI * 330.0 * t);
        vocR[i] = vocL[i];
        basL[i] = 0.45f * (float)Math.Sin(2.0 * Math.PI * 72.0 * t);
        basR[i] = basL[i];
        gtrL[i] = synthSig[i] * (float)Math.Cos(gPhi);
        gtrR[i] = synthSig[i] * (float)Math.Sin(gPhi);
        pnoL[i] = synthSig[i] * (float)Math.Cos(pPhi);
        pnoR[i] = synthSig[i] * (float)Math.Sin(pPhi);
    }
    List<StereoStemBuffer> multiStems = [
        new StereoStemBuffer("vocals", vocL, vocR),
        new StereoStemBuffer("bass", basL, basR),
        new StereoStemBuffer("guitar", gtrL, gtrR),
        new StereoStemBuffer("piano", pnoL, pnoR)
    ];

    IReadOnlyList<LyricMoodDigest> anthemMoods = [new LyricMoodDigest(LyricMoodTag.Anthem, 0.90f, 0.88f, 0, 0.0)];
    IReadOnlyList<LyricMoodDigest> intimateMoods = [new LyricMoodDigest(LyricMoodTag.Intimate, 0.20f, 0.30f, 0, 0.0)];
    SpatialChoreoTrack anthemTrack = NeuralStemSpatializer.BuildChoreoTrack(multiStems, sr, SpatialChoreoMode.Cinematic, anthemMoods);
    SpatialChoreoTrack intimateTrack = NeuralStemSpatializer.BuildChoreoTrack(multiStems, sr, SpatialChoreoMode.Cinematic, intimateMoods);

    float maxSlewDegPerSec = 0f;
    float maxVocalAz = 0f;
    float maxBassAz = 0f;
    float maxBassEl = 0f;
    double dt = 0.05;
    for (int s = 0; s < multiStems.Count; s++) {
        anthemTrack.EvaluatePositionZeroAlloc(s, 0.0, out Vector3 prevPos);
        PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(prevPos), out float prevAz, out _);
        for (double t = dt; t <= 3.8; t += dt) {
            anthemTrack.EvaluatePositionZeroAlloc(s, t, out Vector3 curPos);
            PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(curPos), out float curAz, out float curEl);
            float dAz = MathF.Abs(curAz - prevAz);
            if (dAz > 180f) dAz = 360f - dAz;
            float slew = dAz / (float)dt;
            if (slew > maxSlewDegPerSec) maxSlewDegPerSec = slew;
            if (s == 0) maxVocalAz = MathF.Max(maxVocalAz, MathF.Abs(curAz));
            if (s == 1) {
                maxBassAz = MathF.Max(maxBassAz, MathF.Abs(curAz));
                maxBassEl = MathF.Max(maxBassEl, MathF.Abs(curEl));
            }
            prevAz = curAz;
        }
    }
    Assert(maxSlewDegPerSec <= 62.0f, $"GT-15 choreography slew exceeded 60 deg/s ceiling: {maxSlewDegPerSec:F1} deg/s");
    Assert(maxVocalAz <= 22.5f, $"GT-15 lead vocal azimuth exceeded 22 deg anchor limit: {maxVocalAz:F1} deg");
    Assert(maxBassAz <= 15.5f && maxBassEl <= 0.5f, $"GT-15 bass anchor violated (az={maxBassAz:F1}, el={maxBassEl:F1})");

    // Verify 0 B RT GC during real-time pose sampling
    anthemTrack.EvaluatePositionZeroAlloc(2, 0.25, out _);
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    float accum = 0f;
    for (int step = 0; step < 500; step++) {
        anthemTrack.EvaluatePositionZeroAlloc(step & 3, (step % 75) * 0.05, out Vector3 p);
        accum += p.X + p.Y + p.Z;
    }
    long rtAllocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Assert(rtAllocBytes == 0 && float.IsFinite(accum), $"GT-15 RT choreo sampling must allocate 0 B GC, got {rtAllocBytes} B");

    // Mood expressiveness contrast: Anthem elevates higher than Intimate
    anthemTrack.EvaluatePositionZeroAlloc(2, 1.5, out Vector3 antPos);
    intimateTrack.EvaluatePositionZeroAlloc(2, 1.5, out Vector3 intPos);
    PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(antPos), out _, out float antEl);
    PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(intPos), out _, out float intEl);
    Assert(antEl > intEl + 1.5f, $"GT-15 mood expressiveness: Anthem elevation ({antEl:F1}°) must exceed Intimate ({intEl:F1}°)");

    return new Dictionary<string, object> {
        ["maxFixedPanErrDeg"] = Math.Round(maxPanErrDeg, 3),
        ["maxSlewDegPerSec"] = Math.Round(maxSlewDegPerSec, 2),
        ["maxVocalAzDeg"] = Math.Round(maxVocalAz, 2),
        ["maxBassAzDeg"] = Math.Round(maxBassAz, 2),
        ["anthemVsIntimateElDeltaDeg"] = Math.Round(antEl - intEl, 2),
        ["rtAllocBytes"] = rtAllocBytes
    };
});

// GT-16: W53.6 Wave F — Physical Transaural XTC Speaker Cancellation (>= 10 dB) & Bit-Exact 0.0f Headphone Bypass (KILL R3)
RunSuite("GT-16", "W53_6_WaveF_TransauralXtc_Cancellation_And_BitExactHeadphoneBypass", () => {
    const int sr = 48000;
    const int frames = 8192;
    double[] probeFreqs = [400.0, 800.0, 1500.0, 2800.0];
    float[] multiToneLeft = new float[frames * 2];
    for (int f = 0; f < frames; f++) {
        double t = f / (double)sr;
        double sum = 0.0;
        for (int k = 0; k < probeFreqs.Length; k++) {
            sum += 0.18 * Math.Sin(2.0 * Math.PI * probeFreqs[k] * t);
        }
        multiToneLeft[f * 2] = (float)sum;
        multiToneLeft[f * 2 + 1] = 0f;
    }

    double MeasureSepImprovementDb(
        Cavern.Virtualizer.TransauralSpeakerPreset preset,
        float[] probeStereo,
        int sampleRate,
        int frameCount,
        out double rawSepDb,
        out double xtcSepDb) {
        using Cavernize.Logic.Spatial.Adapters.TransauralXtcPostProcessingStage stage = new();
        stage.Configure(sampleRate, 2);
        stage.ApplyConfig(new Cavernize.Logic.Spatial.TransauralXtcConfig {
            Enabled = true,
            Preset = preset,
            Strength = 0.90f,
            AutoBypassOnHeadphones = true
        });
        stage.UpdateOutputRoute(headphoneRoute: false, AudioDeviceTransport.BuiltinSpeaker);

        Cavern.Virtualizer.TransauralGeometryParams geo = Cavern.Virtualizer.TransauralXtcFilter.ComputeGeometry(preset);
        float[] rawEars = new float[frameCount * 2];
        Cavern.Virtualizer.TransauralXtcFilter.SimulatePhysicalSpeakerToEar(probeStereo, rawEars, frameCount, sampleRate, geo);

        float[] xtcSpk = (float[])probeStereo.Clone();
        stage.ProcessInterleaved(xtcSpk, 0, frameCount, 2, default);
        float[] xtcEars = new float[frameCount * 2];
        Cavern.Virtualizer.TransauralXtcFilter.SimulatePhysicalSpeakerToEar(xtcSpk, xtcEars, frameCount, sampleRate, geo);

        double rawL = 0, rawR = 0, xtcL = 0, xtcR = 0;
        for (int f = 1024; f < frameCount; f++) {
            float rl = rawEars[f * 2], rr = rawEars[f * 2 + 1];
            float xl = xtcEars[f * 2], xr = xtcEars[f * 2 + 1];
            Assert(float.IsFinite(xl) && float.IsFinite(xr), $"GT-16 non-finite output on {preset}");
            rawL += (double)rl * rl; rawR += (double)rr * rr;
            xtcL += (double)xl * xl; xtcR += (double)xr * xr;
        }
        rawSepDb = 10.0 * Math.Log10((rawL + 1e-18) / (rawR + 1e-18));
        xtcSepDb = 10.0 * Math.Log10((xtcL + 1e-18) / (xtcR + 1e-18));
        return xtcSepDb - rawSepDb;
    }

    double phoneGainDb = MeasureSepImprovementDb(
        Cavern.Virtualizer.TransauralSpeakerPreset.PhoneBuiltInLandscape,
        multiToneLeft, sr, frames, out double phoneRawDb, out double phoneXtcDb);
    double deskGainDb = MeasureSepImprovementDb(
        Cavern.Virtualizer.TransauralSpeakerPreset.DesktopMonitors,
        multiToneLeft, sr, frames, out double deskRawDb, out double deskXtcDb);

    Assert(phoneGainDb >= 10.0, $"GT-16 PhoneBuiltInLandscape XTC cancellation gain {phoneGainDb:F2} dB < 10.0 dB");
    Assert(deskGainDb >= 10.0, $"GT-16 DesktopMonitors XTC cancellation gain {deskGainDb:F2} dB < 10.0 dB");

    // Verify KILL R3: Bit-exact 0.0f headphone bypass + 0 B RT GC + < 1.0 ms per 1024-frame block
    using Cavernize.Logic.Spatial.Adapters.TransauralXtcPostProcessingStage rtStage = new();
    rtStage.Configure(sr, 2);
    rtStage.ApplyConfig(new Cavernize.Logic.Spatial.TransauralXtcConfig {
        Enabled = true,
        Preset = Cavern.Virtualizer.TransauralSpeakerPreset.PhoneBuiltInLandscape,
        Strength = 0.90f,
        AutoBypassOnHeadphones = true
    });
    rtStage.UpdateOutputRoute(headphoneRoute: true, AudioDeviceTransport.UsbDac);
    float[] hpCheck = (float[])multiToneLeft.Clone();
    rtStage.ProcessInterleaved(hpCheck, 0, frames, 2, default);
    double maxHpErr = 0.0;
    for (int i = 0; i < hpCheck.Length; i++) {
        if (BitConverter.SingleToInt32Bits(hpCheck[i]) != BitConverter.SingleToInt32Bits(multiToneLeft[i])) {
            maxHpErr = Math.Max(maxHpErr, Math.Abs(hpCheck[i] - multiToneLeft[i]) + 1e-9);
        }
    }
    Assert(maxHpErr == 0.0, $"GT-16 KILL R3 VIOLATION: headphone bypass error={maxHpErr}");

    rtStage.UpdateOutputRoute(headphoneRoute: false, AudioDeviceTransport.BuiltinSpeaker);
    float[] rtBlock = new float[1024 * 2];
    Array.Copy(multiToneLeft, rtBlock, rtBlock.Length);
    rtStage.ProcessInterleaved(rtBlock, 0, 1024, 2, default); // warmup
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    long tStart = Stopwatch.GetTimestamp();
    for (int iter = 0; iter < 50; iter++) {
        rtStage.ProcessInterleaved(rtBlock, 0, 1024, 2, default);
    }
    double msPerBlock = ((Stopwatch.GetTimestamp() - tStart) * 1000.0 / Stopwatch.Frequency) / 50.0;
    long rtAllocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Assert(rtAllocBytes == 0, $"GT-16 RT XTC must allocate 0 B GC, got {rtAllocBytes} B");
    Assert(msPerBlock < 1.0, $"GT-16 RT XTC block time {msPerBlock:F3} ms >= 1.0 ms budget");

    return new Dictionary<string, object> {
        ["phoneRawSepDb"] = Math.Round(phoneRawDb, 2),
        ["phoneXtcSepDb"] = Math.Round(phoneXtcDb, 2),
        ["phoneCancellationGainDb"] = Math.Round(phoneGainDb, 2),
        ["desktopRawSepDb"] = Math.Round(deskRawDb, 2),
        ["desktopXtcSepDb"] = Math.Round(deskXtcDb, 2),
        ["desktopCancellationGainDb"] = Math.Round(deskGainDb, 2),
        ["headphoneBypassErr"] = maxHpErr,
        ["rtAllocBytes"] = rtAllocBytes,
        ["msPer1024Block"] = Math.Round(msPerBlock, 4)
    };
});

if (!string.IsNullOrEmpty(jsonOutPath)) {
    File.WriteAllText(jsonOutPath, JsonSerializer.Serialize(new { passed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
}

Console.WriteLine($"\nAUDIO GROUND TRUTH SUMMARY: {passed} PASSED, {failed} FAILED.");
return failed == 0 ? 0 : 1;
"""


def _validate_iamf_files_with_ffmpeg(iamf_dir: Path, verbose: bool) -> dict[str, object]:
    ffmpeg = shutil.which("ffmpeg")
    ffprobe = shutil.which("ffprobe")
    if not ffmpeg or not ffprobe:
        raise RuntimeError("ffmpeg/ffprobe not found on PATH for iamf-roundtrip validation.")
    metrics: dict[str, object] = {}
    for fname in ("iamf_714.iamf", "iamf_hoa16.iamf"):
        fpath = iamf_dir / fname
        if not fpath.is_file():
            raise RuntimeError(f"Expected generated IAMF artifact missing: {fpath}")
        pr = subprocess.run(
            [ffprobe, "-v", "error", "-show_format", "-show_streams", str(fpath)],
            capture_output=True, text=True, check=False,
        )
        if pr.returncode != 0 or "Failed to read obu type 1" in pr.stderr or pr.stderr.strip():
            raise RuntimeError(f"ffprobe failed on {fname} (rc={pr.returncode}): {pr.stderr.strip()}")
        dec = subprocess.run(
            [ffmpeg, "-v", "error", "-i", str(fpath), "-f", "null", "-"],
            capture_output=True, text=True, check=False,
        )
        if dec.returncode != 0 or "Failed to read obu type 1" in dec.stderr or dec.stderr.strip():
            raise RuntimeError(f"ffmpeg decode failed on {fname} (rc={dec.returncode}): {dec.stderr.strip()}")
        metrics[f"{fname}_bytes"] = fpath.stat().st_size
        if verbose:
            print(f"    ffprobe/ffmpeg validated {fname} ({metrics[f'{fname}_bytes']} bytes, 0 OBU errors)")
    return metrics


def _run_export_compression_roundtrip(verbose: bool) -> dict[str, object]:
    ffmpeg = shutil.which("ffmpeg")
    ffprobe = shutil.which("ffprobe")
    if not ffmpeg or not ffprobe:
        raise RuntimeError("ffmpeg/ffprobe required for export-compression-roundtrip.")

    with tempfile.TemporaryDirectory(prefix="cavern_exp_cmp_") as td:
        p = Path(td)
        wav2 = p / "master_2ch.wav"
        wav12 = p / "master_12ch.wav"
        subprocess.run([
            ffmpeg, "-y", "-v", "error", "-f", "lavfi",
            "-i", "sine=frequency=440:duration=2:sample_rate=48000",
            "-ac", "2", "-c:a", "pcm_s24le", str(wav2),
        ], check=True)
        subprocess.run([
            ffmpeg, "-y", "-v", "error", "-f", "lavfi",
            "-i", "sine=frequency=440:duration=2:sample_rate=48000",
            "-ac", "12", "-c:a", "pcm_s24le", str(wav12),
        ], check=True)

        # Lossless targets: .flac (2ch), .wv (12ch), iamf(fLaC) (2ch)
        out_flac = p / "out.flac"
        out_wv = p / "out.wv"
        out_iamf_flac = p / "out_flac.iamf"
        subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(wav2), "-c:a", "flac", str(out_flac)], check=True)
        subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(wav12), "-c:a", "wavpack", str(out_wv)], check=True)
        subprocess.run([
            ffmpeg, "-y", "-v", "error", "-i", str(wav2), "-map", "0:a",
            "-stream_group", "type=iamf_audio_element:id=1:st=0:audio_element_type=channel,layer=ch_layout=stereo",
            "-stream_group", "type=iamf_mix_presentation:id=2:stg=0:annotations=en-us=Mix,submix=parameter_id=100:parameter_rate=48000:default_mix_gain=0|element=stg=0:parameter_id=101:parameter_rate=48000:default_mix_gain=0:headphones_rendering_mode=stereo:annotations=en-us=Elem|layout=sound_system=stereo",
            "-c:a", "flac", "-f", "iamf", str(out_iamf_flac),
        ], check=True)

        def _decode_s32(src: Path) -> bytes:
            return subprocess.check_output([ffmpeg, "-v", "error", "-i", str(src), "-f", "s32le", "-acodec", "pcm_s32le", "-"])

        ref2 = _decode_s32(wav2)
        ref12 = _decode_s32(wav12)

        def _null_snr_db(ref_b: bytes, dec_b: bytes, label: str) -> float:
            if len(ref_b) != len(dec_b):
                raise AssertionError(f"{label} sample drift detected: ref_bytes={len(ref_b)} != dec_bytes={len(dec_b)}")
            n = len(ref_b) // 4
            rv = struct.unpack(f"<{n}i", ref_b)
            dv = struct.unpack(f"<{n}i", dec_b)
            sq_ref = sum(v * v for v in rv)
            sq_err = sum((a - b) * (a - b) for a, b in zip(rv, dv))
            return 200.0 if sq_err == 0 else 10.0 * math.log10(sq_ref / sq_err)

        snr_flac = _null_snr_db(ref2, _decode_s32(out_flac), "flac")
        snr_wv = _null_snr_db(ref12, _decode_s32(out_wv), "wavpack")
        snr_iamf_flac = _null_snr_db(ref2, _decode_s32(out_iamf_flac), "iamf(fLaC)")
        for label, snr in (("flac", snr_flac), ("wavpack", snr_wv), ("iamf(fLaC)", snr_iamf_flac)):
            if snr < 140.0:
                raise AssertionError(f"{label} null-test SNR {snr:.2f} dB < 140.0 dB")

        # Lossy targets: .opus, .m4a, iamf(Opus) -> >=85% size reduction vs 24-bit PCM master
        out_opus = p / "out.opus"
        out_m4a = p / "out.m4a"
        out_iamf_opus = p / "out_opus.iamf"
        subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(wav2), "-c:a", "libopus", "-b:a", "96k", str(out_opus)], check=True)
        subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(wav2), "-c:a", "aac", "-b:a", "128k", str(out_m4a)], check=True)
        subprocess.run([
            ffmpeg, "-y", "-v", "error", "-i", str(wav2), "-map", "0:a",
            "-stream_group", "type=iamf_audio_element:id=1:st=0:audio_element_type=channel,layer=ch_layout=stereo",
            "-stream_group", "type=iamf_mix_presentation:id=2:stg=0:annotations=en-us=Mix,submix=parameter_id=100:parameter_rate=48000:default_mix_gain=0|element=stg=0:parameter_id=101:parameter_rate=48000:default_mix_gain=0:headphones_rendering_mode=stereo:annotations=en-us=Elem|layout=sound_system=stereo",
            "-c:a", "libopus", "-b:a", "96k", "-f", "iamf", str(out_iamf_opus),
        ], check=True)

        master2_bytes = wav2.stat().st_size
        cuts: dict[str, float] = {}
        for lbl, fpath in (("opus", out_opus), ("m4a", out_m4a), ("iamf(Opus)", out_iamf_opus)):
            pr = subprocess.run([ffprobe, "-v", "error", "-show_streams", str(fpath)], capture_output=True, text=True, check=False)
            if pr.returncode != 0:
                raise RuntimeError(f"ffprobe failed on {lbl}: {pr.stderr.strip()}")
            cut = 1.0 - (fpath.stat().st_size / master2_bytes)
            if cut < 0.85:
                raise AssertionError(f"{lbl} size reduction {cut * 100:.2f}% < 85.0%")
            cuts[lbl] = round(cut * 100.0, 2)

        metrics = {
            "flacSnrDb": round(snr_flac, 2),
            "wavpack12chSnrDb": round(snr_wv, 2),
            "iamfFlacSnrDb": round(snr_iamf_flac, 2),
            "opusSizeCutPct": cuts["opus"],
            "m4aSizeCutPct": cuts["m4a"],
            "iamfOpusSizeCutPct": cuts["iamf(Opus)"],
            "sampleDrift": 0,
        }
        if verbose:
            print(f"    {metrics}")
        return metrics


def _run_proaudio_route_fsm_invariants(verbose: bool) -> dict[str, object]:
    audio_out = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Audio" / "AndroidAudioOutput.cs").read_text(encoding="utf-8")
    bridge_cs = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Audio" / "NativeProAudioBridge.cs").read_text(encoding="utf-8")
    api_h = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Native" / "cavernize_proaudio" / "cavernize_proaudio_api.h").read_text(encoding="utf-8")
    ipc_cs = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "OmniBridgeIpcReceiver.cs").read_text(encoding="utf-8")

    if "class AudioRouteFsm" not in audio_out or "Interlocked.Increment(ref routeEpoch)" not in audio_out or "Interlocked.CompareExchange" not in audio_out:
        raise AssertionError("AudioRouteFsm monotonic routeEpoch CAS invariants missing in AndroidAudioOutput.cs")
    if "ProAudioMicSource.PhoneMic" not in audio_out or "preferredInputDeviceId = 0" not in audio_out:
        raise AssertionError("PhoneMic preferredInputDeviceId=0 reset missing in AndroidAudioOutput.cs")
    if "InstallSigpipeShield()" not in bridge_cs or "NativeInstallSigpipeShield()" not in bridge_cs:
        raise AssertionError("InstallSigpipeShield missing in NativeProAudioBridge.cs")
    if "sigaction(SIGPIPE" not in api_h or "SIG_IGN" not in api_h:
        raise AssertionError("SIGPIPE -> SIG_IGN shield missing in cavernize_proaudio_api.h")
    if 'ExtraBridgeProtoVer = "X-Bridge-Proto-Ver"' not in ipc_cs or 'BridgeProtoVersion = "2.2"' not in ipc_cs:
        raise AssertionError('X-Bridge-Proto-Ver="2.2" constant missing in OmniBridgeIpcReceiver.cs')

    metrics = {"routeEpochCas": True, "phoneMicInIdReset": True, "sigpipeShield": True, "bridgeProtoVer": "2.2"}
    if verbose:
        print(f"    {metrics}")
    return metrics


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
    parser = argparse.ArgumentParser(description="Run Cavern Audio Ground-Truth Pairwise Verification Harness (.NET)")
    parser.add_argument("--verbose", action="store_true", help="Print all numeric metrics per suite")
    parser.add_argument("--suite", default="", help="Run a single suite ID (e.g., GT-05, iamf-roundtrip, export-compression-roundtrip, proaudio-route-fsm, spatial-cortex-rt)")
    parser.add_argument("--json", default="", help="Optional JSON report output path")
    parser.add_argument("--no-cache", action="store_true", help="Force full restore/rebuild and bypass incremental build cache")
    args = parser.parse_args()

    suite_req = args.suite.strip()
    py_results: list[dict[str, object]] = []
    py_passed = 0
    py_failed = 0

    def _run_py_suite(sid: str, title: str, fn) -> None:
        nonlocal py_passed, py_failed
        print(f"[{sid}] {title} ... ", end="", flush=True)
        try:
            m = fn()
            m["id"] = sid
            m["title"] = title
            m["status"] = "PASS"
            py_results.append(m)
            py_passed += 1
            print("PASS", flush=True)
        except Exception as exc:
            py_failed += 1
            py_results.append({"id": sid, "title": title, "status": "FAIL", "error": str(exc)})
            print(f"FAIL -> {exc}", flush=True)

    cs_suites = re.findall(r'RunSuite\("([^"]+)"', HARNESS_PROGRAM_CS)
    cs_aliases = {"spatial-cortex-rt"}
    req_parts = [p.strip().lower() for p in suite_req.split(",") if p.strip()]
    run_cs = (not suite_req) or any(
        p in cs_aliases or any(p == s.lower() for s in cs_suites)
        for p in req_parts
    )

    rc = 0
    cs_json: dict[str, object] = {"passed": 0, "failed": 0, "results": []}

    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    iamf_dir = CACHE_DIR / "iamf_out"
    iamf_dir.mkdir(parents=True, exist_ok=True)
    cs_json_path = CACHE_DIR / "cs_gt.json"

    if run_cs:
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
        if dotnet == "/root/.dotnet/dotnet" and platform.machine().lower() in ("aarch64", "arm64"):
            env.setdefault("DOTNET_GCHeapHardLimit", "20000000")
        env["GT_VERBOSE"] = "1" if args.verbose else "0"
        env["GT_SUITE_FILTER"] = suite_req
        env["GT_JSON_OUT"] = str(cs_json_path)
        env["GT_IAMF_OUT_DIR"] = str(iamf_dir)

        LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOCK_PATH.open("a+", encoding="utf-8") as lock_file:
            try:
                fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                print("[lock] waiting for active PRoot dotnet build...", file=sys.stderr, flush=True)
                fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX)
            try:
                asm_name = "AudioGroundTruth"
                fp_path = CACHE_DIR / ".fingerprint"
                fingerprint = _compute_fingerprint(Path(__file__), dotnet, tfm)
                cached_fp = fp_path.read_text(encoding="utf-8").strip() if fp_path.is_file() else ""
                run_cmd = _find_cached_run_cmd(CACHE_DIR, tfm, asm_name, dotnet)

                if not args.no_cache and cached_fp == fingerprint and run_cmd is not None:
                    print(f"[audio-gt] Cache hit ({tfm}); skipping dotnet build.", flush=True)
                else:
                    csproj_path = CACHE_DIR / f"{asm_name}.csproj"
                    program_path = CACHE_DIR / "Program.cs"
                    assets_path = CACHE_DIR / "obj" / "project.assets.json"
                    csproj_text = HARNESS_CSPROJ.format(
                        tfm=tfm,
                        cavern_csproj=cavern_csproj,
                        logic_csproj=logic_csproj,
                    )
                    csproj_changed = _write_if_changed(csproj_path, csproj_text)
                    _write_if_changed(program_path, HARNESS_PROGRAM_CS)

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
                    print(f"[audio-gt] Building: {' '.join(build_cmd)}", flush=True)
                    build_proc = subprocess.run(build_cmd, cwd=str(REPO_ROOT), env=env)
                    if build_proc.returncode != 0 and use_no_restore:
                        print("[audio-gt] --no-restore build failed; retrying without --no-restore...", flush=True)
                        build_proc = subprocess.run(base_build_cmd, cwd=str(REPO_ROOT), env=env)
                    if build_proc.returncode != 0:
                        fp_path.unlink(missing_ok=True)
                        return build_proc.returncode
                    fp_path.write_text(fingerprint, encoding="utf-8")
                    run_cmd = _find_cached_run_cmd(CACHE_DIR, tfm, asm_name, dotnet)
                    if run_cmd is None:
                        print(
                            f"[audio-gt] ERROR: Built executable not found under {CACHE_DIR / 'bin' / 'Release' / tfm}",
                            file=sys.stderr,
                        )
                        return 1

                cs_json_path.unlink(missing_ok=True)
                print(f"[audio-gt] Running {tfm} Ground-Truth Harness...", flush=True)
                proc = subprocess.run(run_cmd, cwd=str(REPO_ROOT), env=env)
                rc = proc.returncode
                if cs_json_path.is_file():
                    cs_json = json.loads(cs_json_path.read_text(encoding="utf-8"))
            finally:
                fcntl.flock(lock_file.fileno(), fcntl.LOCK_UN)

    if rc == 0 and (not suite_req or suite_req.lower() == "iamf-roundtrip"):
        _run_py_suite("iamf-roundtrip-ffmpeg", "FFmpeg_FFprobe_IAMF_714_And_HOA16_Validation", lambda: _validate_iamf_files_with_ffmpeg(iamf_dir, args.verbose))

    if not suite_req or suite_req.lower() == "export-compression-roundtrip":
        _run_py_suite("export-compression-roundtrip", "FFmpeg_Lossless_FLAC_WV_IAMF_And_Lossy_Opus_M4A_Roundtrip", lambda: _run_export_compression_roundtrip(args.verbose))

    if not suite_req or suite_req.lower() == "proaudio-route-fsm":
        _run_py_suite("proaudio-route-fsm-invariants", "ProAudio_AudioRouteFsm_PhoneMic_SIGPIPE_And_BridgeProto22", lambda: _run_proaudio_route_fsm_invariants(args.verbose))

    total_passed = int(cs_json.get("passed", 0)) + py_passed
    total_failed = int(cs_json.get("failed", 0)) + py_failed
    if suite_req and total_passed + total_failed == 0:
        print(f"[audio-gt] ERROR: --suite '{suite_req}' matched 0 suites.", file=sys.stderr)
        return 1

    if args.json:
        merged = {
            "passed": total_passed,
            "failed": total_failed,
            "results": list(cs_json.get("results", [])) + py_results,
        }
        Path(args.json).write_text(json.dumps(merged, indent=2), encoding="utf-8")

    return 1 if (rc != 0 or py_failed > 0) else 0


if __name__ == "__main__":
    sys.exit(main())
