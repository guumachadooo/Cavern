# Omniscient Spatial Object Cortex (`BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`).

## 0. Meta & Lineage
BP_META|id=BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX|domain_vec=ai-ml:1.00,dsp-hw:0.80,ux-product:0.40|door=1-way|tier=T3|status=SOTA-v1|h1_wave=W0
PARENT_ADR|none
- `ai-ml`: Use AI ONLY where problem invariants generalize (0 per-song/user retraining); bound tokens/VRAM/latency; prove delta vs deterministic heuristic.
- `dsp-hw`: Split Offline / RT 0-alloc 0-lock hot-path (<1ms/block) / Async control; fuse owned signals/hw; prove math in offline golden bench first.
- `ux-product`: Progressive disclosure (single primary CTA, zero jargon), offline/error clarity, <=3 taps to value, 60/120Hz zero-jank & 100% l10n parity.

## 1. Value & Non-Goals
VALUE|Evolve static `NeuralStemSpatializer` into 3-layer musical+semantic Atmos/IAMF choreography engine (`<0.10ms/blk` RT, `0B` GC, `<=1400MB` peak RSS+dmabuf, `0` per-song retraining).
WARDLEY|Commodity=ORT1.32.0/BeatThis-small0(FP16~5.3MB)+BS.2076 ADM+IAMF v1.0.0 OBUs; Differentiator=Layer1 SSM+CNTC stem digest, Layer2 DFA-locked INT4 Director w/ distilled enums, Layer3 Psychoacoustic Governor + 3D geodesic spline LUT.
NON_GOAL|Zero per-song model fine-tuning, cloud audio upload, or video-LLM tokenization (>1.5GB VRAM/network dependency).
NON_GOAL|Zero 8D continuous circular stem sweeps or raw un-clamped LLM coordinates driving RT DSP or ADM/IAMF writers.
NON_GOAL|Zero unverified dynamic `OBJECT_BASED` (type=2) IAMF v1.0.0 muxing (AOMedia IAMF v1.0.0/v1.1.0 permits `CHANNEL_BASED=0`/`SCENE_BASED=1` ONLY; v2.0.0 gated behind runtime flag).
NON_GOAL|Strictly 3D spatial object choreography ONLY (`SpatialSceneHub`/ADM/IAMF) — ZERO Phase 3 Vocal Cortex, vocal FX/plugin unification, live feedback shield (`2D-SHIELD`), or DAW plugin capture (`W30-W44` deferred).

## 2. Live SOTA & Blind Morphogenesis
SOTA_SOURCES|`Beat-This!` small0 FP16(~5.3MB, Conv+Attn ONNX-clean vs `All-In-One` NATTEN custom op blocked on Android ORT); `ORT GenAI` QNN HTP v79+ / `LiteRT-LM` INT4; ITU-R BS.2076 ADM BWF + AOMedia IAMF v1.0.0/v2.0.0 specs; `Cavern/Spatial/SpatialXRayPowermap.cs`, `Cavern/Neural/NeuralStemSpatializer.cs`, `NeuralTrackCacheService.cs` (`CNTC`), `BroadcastWaveFormatWriter.cs`, `IamfEnvironmentWriter.cs`.
TRIZ_CONTRADICTION|Rich musical/lyrical spatial choreography vs S25U `11.1GB` RAM/Vulkan-dmabuf/thermal ceiling & `<1ms` `0B`-GC RT phase/mono invariants -> Time-separation (`Plane1A -> dispose -> Plane1B -> .ntc CNTCv2`) + Hierarchy-separation (DFA-locked bar/phrase `DirectorCue[]` -> deterministic Layer-3 ODE/Governor LUT).
CANDIDATE|A|Deterministic Audio-Physics + Micro-ONNX Only|`Beat-This! small0` FP16 (`5.3MB`, `0MB` C# spectral-flux fallback) + C# MFCC/Chroma SSM + `LyricsService` prosody/2KB lexicon -> Layer3 spring-Coulomb LUT -> `SpatialSceneHub` / ADM / 16ch HOA IAMF.
REJECTED_BECAUSE|A|Lexicon+prosody fails narrative/metaphor shifts, sarcasm, & vocal-adlib call-response spatialization across verses/bridges (0% semantic comprehension outside keyword table).
CANDIDATE|B|Unconditional Local INT4 1B-3B Director|`Gemma-3-1B/3B` INT4 (`550-1800MB`) running co-resident w/ `scnet_4stem_fp16` (`301MB`) + `uvr_mdxnet_kara_2` (`374MB`) emitting per-frame JSON cues into ADM/IAMF writers.
REJECTED_BECAUSE|B|Co-resident stems + LLM + Dawn/Vulkan `dmabuf` slabs exceed `1.8GB` -> S25U `lmkd` SIGKILL (`>1.6GB`) & SELinux `cdsp`/`QnnHtp` block causes CPU thermal throttling (`>46C`), plus raw polar `(az,el)` decimation causes chord-through-head & polar collapse.
WINNER|Hybrid 3-Plane/3-Layer Cortex: **Plane1A** (`BeatThis-small0` `5.3MB` + `0MB` flux fallback + C# SSM + `CNTCv1` stems + distilled `LyricsService` valence/arousal enums, isolated/released pre-1B) -> **Plane1B** (optional serialized `Gemma-3-1B-IT` INT4 on SD8E HTP w/ CPU DFA JSON mask capped `<=256 tok` & `<=8 cues/min`, auto-falling back to Layer1 rules when cold/blocked) -> **Plane3** (`<2ms` Psychoacoustic Governor: Lead `|az|<=22deg`, Bass `<120Hz` mono `|az|<=15deg,el=0`, slew `<=60deg/s`, `12x6` ERB repulsion, 3D unit-SLERP LUT + BS.1770 `-1.0 dBTP`) -> **Plane2 RT** (`<0.10ms/blk`, `0B` GC) + geodesic 3D ADM & 16ch lossless HOA/7.1.4 IAMF v1.0.0.

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|ORT WebGPU `Dispose()` leaks Dawn/Vulkan `dmabuf` slabs while `Gemma3` 262k-vocab FastRPC HTP->CPU logits copy spikes anon+dmabuf `>2.2GB` -> `lmkd` SIGKILLs mid-`.ntc` write, or SELinux blocks `QnnHtp` forcing CPU INT4 thermal throttling (`>46C`) & Plane2 XRuns, while polar `(az,el)` ADM decimation cuts chords through listener head and HOA sum clips `+4dB`.
THREAT|Lyric Prompt Injection & Spatial Limit-Cycle: malicious `.lrc`/ID3 tags hijack LLM inside valid JSON DFA into `+-180deg` ping-pong (`>0dBFS` vertigo) -> DEF: NEVER feed raw lyrics to LLM; distill `LyricsService` T0-3 into bounded enums (`calm|tense|intimate|anthem`, `arousal/valence 0..1`), cap DFA `<=256 tok` / `<=8 cues/min`, enforce 3D unit-SLERP shortest-arc + `2deg` hysteresis + critically damped ERB governor + C# fallback on NaN/thermal fault.
KILL|R1|RAM+dmabuf+QNN+Thermal|`peak_rss_plus_dmabuf_mb > 1400 OR qnn_init_fail_rate > 0.02 OR p95_cue_ms_per_min > 4500 OR skin_temp_c > 44.0`|50-track album soak on S25U Android 17|Isolate Plane1A in `:worker` proc to reclaim Vulkan `dmabuf`, disable Plane1B LLM -> 100% deterministic Layer1 SSM+Prosody.
KILL|R2|Plane2_RT_Desync+GC+Clip|`rt_block_p99_ms > 0.25 OR rt_gc_alloc_b > 0 OR xruns > 0 OR max_slew_deg_s > 60.0 OR true_peak_dbtp > -0.5`|1000x random seek/rate/route + 30m 11ch DirAc @ 44.1/48/96kHz|Bypass dynamic Plane3 ERB-repulsion & LUT -> pin static C2 preset coords + BS.1770 `-1.0 dBTP` lookahead limiter.
KILL|R3|ADM_Geodesic+IAMF_Coherence|`adm_schema_err > 0 OR geodesic_err_deg > 0.5 OR hoa_folddown_lufs_err > 1.5 OR bass_120hz_xcorr < 0.95`|200-track adversarial suite (`+-180deg` wraps, `el->90deg`, `<120Hz` sub)|Replace polar DP w/ 3D unit-sphere geodesic DP + `jumpPosition=0` cuts; emit `fLaC`/7.1.4 ch-based IAMF v1.0.0 w/ BS.1770 mix norm.

## 4. Invariants, Budgets & QAS
BUDGET|rt_latency<=0.10ms/blk|rt_gc=0B/blk|peak_rss_plus_dmabuf<=1400MB|plane1b_cue<=2800ms/song|skin_temp<=44.0C|true_peak<=-1.0dBTP
PLANES|offline=Plane1A(BeatThis+SSM+enum_digest->unload)+Plane1B(optional_HTP_INT4_DFA->CNTCv2)|rt=Plane2(ring_LUT+12x6_XRay+DirAc_0alloc)|async=Plane3_Governor(<2ms_oneshot)+60Hz_UI_snapshot
QAS|Q1|50-track album spatial choreography prep on S25U|Android 17 + ORT 1.32.0 WebGPU/CPU|Plane1A frees Vulkan dmabuf before Plane1B & writes atomic CNTCv2 or C# fallback|<=1400MB peak RSS+dmabuf & <=2800ms/song P95|python3 -B .agents/scripts/run_w46_unit_tests.py --filter SpatialCortexBudget
QAS|Q2|1000x random seek/rate-change during 11ch DirAc playback|Plane2 RT audio thread 44.1/48/96kHz|Ring-idx SLERP LUT maintains phase continuity & zero GC|<=0.10ms/blk P99 & 0B GC & >=0.95 bass xcorr|python3 -B .agents/scripts/run_audio_ground_truth.py --suite spatial-cortex-rt
QAS|Q3|Adversarial +-180deg azimuth & 90deg zenith cues exported to ADM+IAMF|Offline export to BS.2076 ADM BWF & IAMF v1.0.0|3D geodesic decimation prevents chord-through-head & BS.1770 norm clamps sum|<=0.5deg geodesic err & <=-1.0dBTP true peak & 0 schema errs|python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.90*9|ORT WebGPU Dawn dmabuf release vs `:worker` proc isolation + SELinux `QnnHtp` probe on S25U Android 17|dmabuf delta <=50MB post-Dispose & QNN HTP init <800ms else lock Plane1A `:worker` + deterministic fallback
SPIKE|S2|VoI=0.80*8|3D unit-sphere geodesic ADM decimation vs polar `(az,el)` DP + `ffprobe` BS.2076/IAMF v1.0.0 16ch HOA BS.1770 validation|0 schema errs & <=0.5deg chord error & <=-1.0dBTP across 20 test stems

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W1|Wave-0 Golden Bench + dmabuf/`QnnHtp` S25U SPIKE|synthetic 10-stem vectors + S25U mem probe->golden JSON parity report|python3 -B .agents/scripts/run_audio_ground_truth.py --suite spatial-cortex-bench
SLICE|W2|Layer-1 Audio Forensics + Distilled Lyric Enums (`CNTC v2`)|audio stems + `LyricsService` T0-3->`BeatThis`/SSM + `SpatialChoreoTrack` (`.ntc`)|python3 -B .agents/scripts/run_w46_unit_tests.py --filter SpatialChoreo
SLICE|W3|Layer-3 Psychoacoustic Governor + Geodesic ADM/IAMF Norm|`DirectorCue[]`->unit-SLERP C2 LUT + `12x6` ERB clamp + `-1.0dBTP` BS.1770 norm|dotnet test Cavern.Tests --filter SpatialGovernorTests

### H2+ Outcome Stubs
STUB|W4|Optional Plane1B HTP INT4 semantic director w/ distilled mood/arousal token prompt, DFA `<=256 tok` mask & automatic rule fallback|<=2800ms/song & 0 prompt-injection breaches
STUB|W5|Plane2 zero-GC RT ring-LUT wiring into `SpatialSceneHub` & 3D X-Ray live orbit visualization + 1-tap `Subtle/Musical/Cinematic/Orbit` UI|<=0.10ms/blk & 100% 4-lang l10n parity
STUB|W6|Optional IAMF v2.0.0 dynamic object muxing once `libffmpeg`/reference validator stabilizes object elements|0 `ref_libiamf` bitstream errors

## 7. Fitness Functions & Evolution
FITNESS|`python3 -B .agents/scripts/run_audio_ground_truth.py --suite spatial-cortex-rt && python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings`
EVOLVE_RULE|on(kill_trigger || fitness_fail) -> mark SEED/ADR SUPERSEDED && delta-reenter Stage2-3 (!full rewrite)
