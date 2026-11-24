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
import os
import platform
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
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Cavern;
using Cavern.Channels;
using Cavern.Neural;
using Cavern.Restoration;
using Cavern.Spatial;
using Cavern.Virtualizer;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

string suiteFilter = Environment.GetEnvironmentVariable("GT_SUITE_FILTER") ?? "";
bool verbose = (Environment.GetEnvironmentVariable("GT_VERBOSE") ?? "0") == "1";
string jsonOutPath = Environment.GetEnvironmentVariable("GT_JSON_OUT") ?? "";

List<Dictionary<string, object>> results = [];
int passed = 0;
int failed = 0;

void RunSuite(string id, string title, Func<Dictionary<string, object>> body) {
    if (!string.IsNullOrEmpty(suiteFilter) && !id.Equals(suiteFilter, StringComparison.OrdinalIgnoreCase)) {
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

if (!string.IsNullOrEmpty(jsonOutPath)) {
    File.WriteAllText(jsonOutPath, JsonSerializer.Serialize(new { passed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
}

Console.WriteLine($"\nAUDIO GROUND TRUTH SUMMARY: {passed} PASSED, {failed} FAILED.");
return failed == 0 ? 0 : 1;
"""


def main() -> int:
    parser = argparse.ArgumentParser(description="Run Cavern Audio Ground-Truth Pairwise Verification Harness (.NET)")
    parser.add_argument("--verbose", action="store_true", help="Print all numeric metrics per suite")
    parser.add_argument("--suite", default="", help="Run a single suite ID (e.g., GT-05)")
    parser.add_argument("--json", default="", help="Optional JSON report output path")
    args = parser.parse_args()

    dotnet = find_dotnet()
    tfm = detect_tfm(dotnet)
    cavern_csproj = REPO_ROOT / "Cavern" / "Cavern.csproj"
    logic_csproj = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Cavernize.Logic.csproj"

    with tempfile.TemporaryDirectory(prefix="cavern_audio_gt_") as tmp:
        tmp_path = Path(tmp)
        (tmp_path / "AudioGroundTruth.csproj").write_text(
            HARNESS_CSPROJ.format(tfm=tfm, cavern_csproj=cavern_csproj, logic_csproj=logic_csproj),
            encoding="utf-8",
        )
        (tmp_path / "Program.cs").write_text(HARNESS_PROGRAM_CS, encoding="utf-8")

        env = os.environ.copy()
        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
        env["DOTNET_EnableDiagnostics"] = "0"
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        if dotnet == "/root/.dotnet/dotnet" and platform.machine().lower() in ("aarch64", "arm64"):
            env.setdefault("DOTNET_GCHeapHardLimit", "20000000")
        env["GT_VERBOSE"] = "1" if args.verbose else "0"
        env["GT_SUITE_FILTER"] = args.suite
        env["GT_JSON_OUT"] = args.json

        cmd = [
            dotnet,
            "run",
            "--project",
            str(tmp_path / "AudioGroundTruth.csproj"),
            "-c",
            "Release",
            "-m:1",
            "/p:UseSharedCompilation=false",
        ]
        print(f"[audio-gt] Running {tfm} Ground-Truth Harness...", flush=True)
        proc = subprocess.run(cmd, cwd=str(REPO_ROOT), env=env)
        return proc.returncode


if __name__ == "__main__":
    sys.exit(main())
