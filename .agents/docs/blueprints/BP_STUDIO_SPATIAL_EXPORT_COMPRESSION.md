# Studio Spatial Export 3-Tier Compression (`BP_STUDIO_SPATIAL_EXPORT_COMPRESSION`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`).

## 0. Meta & Lineage
BP_META|id=BP_STUDIO_SPATIAL_EXPORT_COMPRESSION|domain_vec=dsp-hw:1.00,ux-product:0.67,infra-ops:0.50|door=1-way|tier=T3|status=SOTA-v1|h1_wave=W0
PARENT_ADR|none
- `dsp-hw`: Split Offline / RT 0-alloc 0-lock hot-path (<1ms/block) / Async control; fuse owned signals/hw; prove math in offline golden bench first.
- `ux-product`: Progressive disclosure (single primary CTA, zero jargon), offline/error clarity, <=3 taps to value, 60/120Hz zero-jank & 100% l10n parity.
- `infra-ops`: Design for unattended recovery, idempotent deploy/rollback, OOM/quota ceilings, zero-blind-retry backoff & structured observability.

## 1. Value & Non-Goals
VALUE|Cut 361MB Studio spatial exports by `45-65%` (Lossless) and `85-95%` (Portable Lossy) under `<=64MB` peak RSS via streaming chunked sinks and honest container-spec UX.
WARDLEY|Commodity=W^X `libffmpeg.so` v8.1.3 (`flac`, `wavpack`, `libopus`, `aac`) + pure-C# `FlacAudioConverter`; Differentiator=`IStreamingExportSink` (`4096f` pooled pipeline) + AOMedia IAMF `fLaC`/`Opus` OBU muxer + One UI 9 3-Tier progressive selector.
NON_GOAL|Zero lossy transcoding of `DolbyAtmosAdm` (ITU-R BS.2076 mandates uncompressed PCM `axml`/`chna`; zero OSS TrueHD+Atmos encoder exists -> UI warns & redirects 1-tap to `EclipsaIamf`).
NON_GOAL|Zero full-track `float[][]` allocations in `StudioBatchExporter` or new writable `FilesDir/bin/` native drops (strict Android 15-17 SELinux W^X `NativeLibraryDir` only).

## 2. Live SOTA & Blind Morphogenesis
SOTA_SOURCES|AOMedia IAMF v1.0.0 (`fLaC` raw 34-byte `STREAMINFO` w/ `min==max==num_samples_per_frame` & zero `fLaC` magic + `obu_trimming_status_flag`; `Opus` `audio_roll_distance=-ceil(3840/N)` @ `48kHz`); RFC 8486 Opus Ambisonics (`mapping_family` 2/3) & Family 255 discrete; FLAC `STREAMINFO` 3-bit `8ch` hard cap vs FFmpeg native `wavpack` `256ch` lossless; `MainActivity.StudioExport.cs`, `StudioBatchExporter.cs`, `FlacAudioConverter.cs`, `IamfEnvironmentWriter.cs`, `AmbisonicsHoaWriter.cs`.
TRIZ_CONTRADICTION|12-16ch lossless/lossy compression w/ `<=64MB` RAM & zero `2x` storage leaks conflicts w/ full-track `float[][]` buffering (`3.7GB` OOM), FLAC `8ch` limit, & ADM PCM-only mandate -> Segmentation (`4096f` streaming sink + IAMF `<=2ch` substreams) + Intermediary (`NativeLibraryDir/libffmpeg.so` stdin pipe w/ `SIGPIPE` shield + C# direct PCM/FLAC/OBU writers).
CANDIDATE|A|Zero-FFmpeg Direct P/Invoke ABI|`IStreamingExportSink` (`4096f` `ArrayPool`) + compile/bundle 16KB-aligned `libopus.so` & `libwavpack.so` + pure-C# Ogg/Opus & WavPack RIFF chunk muxers (`0` subprocesses).
REJECTED_BECAUSE|A|Bundling/maintaining extra custom `.so` native builds (`+630KB+` APK, NDK ABI matrix) & reimplementing Ogg/Opus/WavPack container muxers duplicates `libffmpeg.so` v8.1.3 already shipped in `NativeLibraryDir`, and `Headphone3D` `.opus` misses universal `.m4a` AAC player support.
CANDIDATE|B|100% FFmpeg Subprocess All Tiers|Spawn `libffmpeg.so` child process for ALL 3 tiers including 2ch WAV/FLAC, ADM RF64, & `channelsplit` to containerized `.flac`/`.ogg` CacheDir scratch files for IAMF OBU assembly.
REJECTED_BECAUSE|B|Naive `channelsplit` to `.flac`/`.ogg` emits `fLaC` magic + variable `block_size` + Opus `312f` pre-skip corrupting IAMF `CodecConfig`/sample parity, while spawning FFmpeg for C# PCM/FLAC/ADM adds fork overhead and `2x` MediaStore+CacheDir disk amplification.
WINNER|Hybrid Chunked Streaming Router (`IStreamingExportSink`, `4096f` private pooled `256KB` blocks, `<=64MB` total RSS): **Direct C# Streaming** (`0` fork, `0` temp WAV, header backpatch on `Dispose`) for Tier 1 Master PCM (`.wav`/RF64 ADM/`.amb`/`ipcm` `.iamf`) & Tier 2 `2-8ch` `.flac`; **W^X Stdin Pipe** to `NativeLibraryDir/libffmpeg.so` (`sigaction(SIGPIPE,SIG_IGN)` + async stderr ring) for Tier 2 `12-16ch` `wavpack` `.wv` (`Cinema714`/`Ambisonics360`, bypassing FLAC `8ch` cap) & Tier 3 Lossy (`Opus` RFC 8486 Family 2/3 & 255 / `.m4a` `aac` w/ `frag_moov`); **Spec-Stripped IAMF Muxer** (native `-f iamf` OR raw `STREAMINFO`/0xFFF8 FLAC & `48kHz` Opus OBU framing verified by `ffprobe` self-parse pre-commit); **One UI 9** 3-pill selector (`Master PCM` | `Lossless` | `Portable Lossy`) + collapsed Codec Details + inline `DolbyAtmosAdm` T2/T3 lock w/ 1-tap `EclipsaIamf` switch.

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|90m `16ch @ 96kHz` export under screen-off hits Android 17 CachedAppsFreezer or `lmkd` on `libffmpeg` (`16ch` encoders RSS `>300MB`) OR `2x` `CacheDir+MediaStore` exhaustion (`ENOSPC`) -> `pipe:0` throws `SIGPIPE/EPIPE` aborting before `IS_PENDING=0` & unlink -> leaks multi-GB invisible `IS_PENDING=1` MediaStore blobs, while non-seekable SAF FDs corrupt `.m4a` `moov` and `FLAC` `total_samples`.
THREAT|Corrupt Float / SIGPIPE / Raw-vs-Container IAMF Mismatch: `NaN/Inf` in pooled PCM crashes encoder, `EPIPE` hangs UI, or `.flac`/`.ogg` magic headers corrupt IAMF `CodecConfig` -> DEF: sanitize `NaN/Inf -> 0f` & clamp `[-1,1]` pre-pipe, install `sigaction(SIGPIPE, SIG_IGN)` in `libcavernize_proaudio.so`, enforce `StatFs` `>= 2.2x est + 512MB` preflight, use seekable `PFD("rw")` / `frag_moov` for `.m4a`, strip `fLaC` headers + set `audio_roll_distance=-ceil(3840/N)` & `obu_trimming_status_flag`, and run `finally` atomic unlink + `ContentResolver.delete(pendingUri)`.
KILL|R1|Pipe_OOM_Orphan_Disk|`sigkill_or_epipe_rate > 1.0% OR child_rss_mb > 192 OR disk_amp > 1.25x OR orphan_bytes > 0`|Wave-0 90m `16ch` screen-off soak + 14d telemetry|Drop `libffmpeg` `channelsplit` scratch -> bind in-process P/Invoke `libopus`/`libwavpack` w/ zero temp disk.
KILL|R2|IAMF_Bitstream_Drift|`ref_libiamf_fail > 0 OR substream_sample_drift != 0 OR flac_null_snr_db < 140.0`|CI matrix (`2/7.1.4/HOA16` x `ipcm/fLaC/Opus` x `44.1/48/96kHz`)|Disable custom C# `fLaC`/`Opus` IAMF OBU muxer -> gate on `libffmpeg -f iamf` or keep `ipcm` `.iamf` + `.wv`/`.opus`.
KILL|R3|MediaStore_SAF_Playback|`mediastore_leak_pct > 0.5% OR a17_player_fail_pct > 2.0%`|S25U Android 17 MediaStore/SAF + player matrix|Route `.iamf`/`.wv` strictly to `Downloads/Cavernize/Renderizados/` (never `Audio.Media`), wrap IAMF in MP4 if needed.

## 4. Invariants, Budgets & QAS
BUDGET|csharp_heap<=16MB|child_rss<=192MB|disk_amp<=1.25x|orphan_pending_bytes=0B|lossless_snr>=140.0dB|lossy_size_cut>=85%
PLANES|offline=IStreamingExportSink(4096f_pooled_256KB_blocks->C#_or_W^X_pipe)|rt=bypassed(playback_stopped_pre_export)|async=MediaProcessing_FGS_progress+atomic_MediaStore_commit
QAS|Q1|90m 12ch `Cinema714` & 16ch `Ambisonics360` Tier 2/3 export under screen-off|S25U Android 17 `NativeLibraryDir/libffmpeg.so` v8.1.3|Streaming 4096f sink keeps heap <=16MB, child <=192MB, & 0 orphan blobs on cancel/ENOSPC|<=16MB C# heap & <=192MB child RSS & 0B orphan leak|python3 -B .agents/scripts/run_w46_unit_tests.py --filter StreamingExportSink
QAS|Q2|Roundtrip decode of Tier 2 `.flac`/`.wv`/`.iamf(fLaC)` vs Tier 1 PCM master|CI parity `dotnet test` + `ffprobe`/`ffmpeg` decode|Bit-exact or lossless residual difference across all channels & exact sample count|>=140.0dB null-test SNR & 0 sample drift|python3 -B .agents/scripts/run_audio_ground_truth.py --suite export-compression-roundtrip
QAS|Q3|One UI 9 3-Tier selector toggled across all 5 Studio base formats + 4 locales|CavernizeAndroid `AndroidStrings*.cs` + `SelfDiagnosticEngine` L8|DolbyAtmosAdm disables Tier 2/3 w/ clear note + 1-tap `EclipsaIamf` redirect; 0 clipped views|100% key parity & <=1 tap switch to EclipsaIamf|python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.95*9|Probe `libffmpeg.so` v8.1.3 (`-encoders`/`-muxers` for `wavpack`, `libopus`, `aac`, `iamf`) + raw vs `-f iamf` OBU `ffprobe` validation|Confirmed encoder availability + 0 `ffprobe` OBU errors on `7.1.4` & `16ch HOA` `fLaC`/`Opus`
SPIKE|S2|VoI=0.80*8|Benchmark `4096f` `IStreamingExportSink` + `SIGPIPE` + `StatFs` + `IS_PENDING` cancel cleanup on 60m `16ch` render|<=16MB C# heap & 0 leaked `IS_PENDING=1` entries after mid-export SIGKILL/cancel

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W1|Wave-0 `libffmpeg.so` Codec/IAMF Probe + `IStreamingExportSink` Core|PCM `float[]` stream->`4096f` chunked sink w/ `NaN` guard & `RIFF/RF64` header backpatch|dotnet test Cavern.Tests --filter StreamingSinkTests
SLICE|W2|W^X Pipe `wavpack`/`opus`/`aac` + C# `FlacStreamEncoder` + `StatFs`/`IS_PENDING` Guard|`StudioBaseOutputFormat` x Tier 1/2/3->`NativeLibraryDir/libffmpeg.so` pipe & atomic commit|python3 -B .agents/scripts/run_audio_ground_truth.py --suite export-compression-roundtrip
SLICE|W3|Spec-Compliant `EclipsaIamf` `fLaC`/`Opus` Muxer + One UI 9 3-Tier Selector|`AndroidStrings*.cs` + `StudioCompressionTier`->OBU framing + ADM T2/T3 warning redirect|python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings

### H2+ Outcome Stubs
STUB|W4|Auto bitrate/size estimator chip & batch preset compression persistence across queue jobs|<=3% size estimate error vs final artifact
STUB|W5|Optional MP4-wrapped IAMF export option for broader Android Media3 hardware playback compatibility|100% playback pass in system MediaExtractor

## 7. Fitness Functions & Evolution
FITNESS|`python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings && python3 -B .agents/scripts/run_audio_ground_truth.py --suite export-compression-roundtrip`
EVOLVE_RULE|on(kill_trigger || fitness_fail) -> mark SEED/ADR SUPERSEDED && delta-reenter Stage2-3 (!full rewrite)
