# Timeless Build & Commit Error Lessons (`BUILD_LESSONS.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim diagnostic codes & symbols).
> LAW|ANTI-OBSOLESCENCE (MANDATORY): NEVER hardcode ephemeral target versions (OS/API level, .NET/TFM, JDK, Actions `@vN`, NDK/lib versions, or calendar dates) as permanent rules. ALWAYS state timeless root cause + version-agnostic rule + dynamic live-lookup instruction (`official docs` / active `.csproj` / workflow / `fetch_live_diagnostics.py --ci`).
> LEARNER|dynamic auto-learning CLI: `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` (CI JSON) | `--from-log <file> --apply` (local build/check log) | `--record ... --apply` (manual structural lesson) | `--audit-lessons` (enforce timelessness + zero duplicates).

---

## 1. Core Timeless Build & Commit Lessons (`BL-001` .. `BL-017`)

### `BL-001` | SDK, TFM, MSBuild & CI Runner Version Drift
- `Category`: `toolchain-ci`
- `Codes`: `XA1006, XA4211, NETSDK1045, NETSDK1139, MSB4018, ACTIONS_DEPRECATED`
- `Keywords`: `TargetFramework, TargetPlatformVersion, targetSdkVersion, RunAOTCompilation, PublishReadyToRun, setup-java, setup-dotnet, workload, node runtime`
- `RootCause`: Cloud CI runners, platform SDK workloads, MSBuild packaging flags, & GitHub Action runtimes evolve continuously; static assumptions or mismatched project TFM vs runner SDK break builds or emit deprecation/SDK-delta warnings.
- `TimelessRule`: Keep project `<TargetFramework>`, MSBuild AOT/publish properties, & CI Action majors aligned w/ the active runner toolchain; NEVER blindly bump `android:targetSdkVersion` outside project compatibility/W^X policy -> either update intentionally after full platform-behavior audit OR suppress expected SDK-vs-targetSdk delta warnings explicitly w/ rationale comment in `.csproj`.
- `DynamicLookup`: Before editing `.csproj` TFMs, AOT/publish properties, `AndroidManifest.xml` SDK levels, or `.github/workflows/*.yml`, fetch live CI diagnostics (`python3 -B .agents/scripts/fetch_live_diagnostics.py --ci`) && verify current official `.NET Android`, Android SDK, & GitHub Actions documentation for supported flags and current stable major versions.
- `AutoGuard`: `file_guards` (`AndroidManifest.xml` W^X targetSdk policy) + CI diagnostics breakdown (`BuildAndroid.yml`)

### `BL-002` | Platform OS Version Guards & Analyzer Scope (`CA1416`)
- `Category`: `android-platform`
- `Codes`: `CA1416`
- `Keywords`: `Build.VERSION.SdkInt, IsAndroidVersionAtLeast, SupportedOSPlatform, lambda, callback, reachable on`
- `RootCause`: Roslyn platform compatibility analyzers ignore Java-style `Build.VERSION.SdkInt` checks && do NOT propagate outer OS-version guards across lambda, local-callback, or helper-method boundaries.
- `TimelessRule`: NEVER guard OS-versioned calls w/ `Build.VERSION.SdkInt` -> ALWAYS use `OperatingSystem.IsAndroidVersionAtLeast(N)` inside the EXACT execution scope (repeat inside lambdas/callbacks/listeners) OR annotate helper/override w/ `[System.Runtime.Versioning.SupportedOSPlatform("androidN.0")]` (and wrap `base.*` calls inside `IsAndroidVersionAtLeast(N)`).
- `DynamicLookup`: Check the API's introducing OS level in live official Android / `.NET Android` docs && compare against `<SupportedOSPlatformVersion>` in `CavernizeAndroid.csproj` before choosing `N`.
- `AutoGuard`: `diff_rules` (`Build.VERSION.SdkInt` warn rule in `.agents/planner_profile.json`)

### `BL-003` | Platform API Obsolescence & Cross-API Compatibility (`CA1422`, `CS0618`)
- `Category`: `android-platform`
- `Codes`: `CA1422, CS0618, SYSLIB`
- `Keywords`: `obsoleted on, deprecated, SetDecorFitsSystemWindows, ScaledDensity, GetParcelableExtra, dead branch, minSdk`
- `RootCause`: Platform bindings obsolete older APIs as OS versions advance; keeping dead branches below `<SupportedOSPlatformVersion>` (`minSdk`), calling APIs superseded by OS defaults, or guessing preview replacements creates warnings or runtime breakage.
- `TimelessRule`: Omit dead `< minSdk` branches; skip calls whose behavior became default at newer OS levels via `!OperatingSystem.IsAndroidVersionAtLeast(obsoleteApi)`; prefer stable timeless replacements (`TypedValue.ApplyDimension`, factory `ValueOf`, typed overloads); ONLY use narrowly scoped `#pragma warning disable CS0618, CA1422` + `#pragma warning restore` w/ inline rationale when cross-API runtime compatibility strictly requires the legacy call.
- `DynamicLookup`: Consult live official Android & `.NET Android` API docs (plus `fetch_live_diagnostics.py --ci`) to verify the current non-obsolete stable replacement (never guess preview-only APIs).
- `AutoGuard`: CI diagnostics `High` warning priority (`CA1422`/`CS0618`)

### `BL-004` | `.NET Android` Namespace Collisions (`CS0104`)
- `Category`: `android-bindings`
- `Codes`: `CS0104`
- `Keywords`: `ambiguous reference, OperationCanceledException, Process, Encoding, Stream, Android.OS, Android.Media`
- `RootCause`: Importing `Android.OS` or `Android.Media` alongside `System.*` (or implicit usings) introduces identically named types (`OperationCanceledException`, `Process`, `Encoding`, `Stream`), causing `CS0104` compile failures.
- `TimelessRule`: In Android C# files, ALWAYS fully qualify collision-prone BCL types (`System.OperationCanceledException`, `System.Diagnostics.Process`, `System.Text.Encoding`, `System.IO.Stream`) instead of relying on unqualified short names.
- `DynamicLookup`: Inspect file `using` directives && run local shadow check (`python3 .agents/scripts/check_android_cs.py`) or grep for `using Android.OS` / `using Android.Media` before introducing BCL types in `CavernizeAndroid/`.
- `AutoGuard`: `diff_rules` (unqualified `Process.Start(` warn in `.agents/planner_profile.json`) + `check_android_cs.py`

### `BL-005` | `.NET Android` Binding & Override Divergences from Java (`CS0115`, `CS0122`, `CS1503`, `CS0117`)
- `Category`: `android-bindings`
- `Codes`: `CS0115, CS0122, CS0507, CS0117, CS1503`
- `Keywords`: `OnMeasure, ScaleX, ScaleY, SetAllCaps, AllCaps, BiometricErrorCode, Paint.Style, SetStroke, ColorStateList, SeekTo, GetFrameAtTime, MetadataKey, TextView.Paint`
- `RootCause`: `.NET Android` bindings differ systematically from Java Android & AndroidX: Java getters/setters map inconsistently (`View.ScaleX` prop vs `TextView.SetAllCaps(bool)` method), inherited properties shadow type names (`TextView.Paint` shadows `Android.Graphics.Paint` inside `Switch`/`TextView` subclasses), override visibility may be `public` (`public override void OnMeasure`), platform enums omit AndroidX-only constants (`BiometricErrorCode` omits `NegativeButton`), && overloads/enums have C#-specific signatures (`SetStroke` `ColorStateList`, `SeekTo(int)`, explicit timestamp on `GetFrameAtTime`, numeric casts when binding enum omits newer keys).
- `TimelessRule`: NEVER transcribe Java Android or AndroidX member names/modifiers/overloads blindly into native `.NET Android` C#; use `SetAllCaps(bool)` (never `AllCaps =`), qualify `Android.Graphics.Paint.Style.*` inside `TextView`/`CompoundButton`/`Switch` subclasses, && verify exact enum members against platform bindings rather than AndroidX docs.
- `DynamicLookup`: Verify exact C# binding member signature against current `.NET Android` API docs (`dotnet/android-api-docs`) or local Roslyn reference assemblies (`python3 .agents/scripts/check_android_cs.py`) before committing.
- `AutoGuard`: `diff_rules` (`AllCaps =` fail rule) + `check_android_cs.py` + CI 4-layer compile & shadow audit

### `BL-006` | Cross-Layer Contract & Symbol Drift (`CS0103`, `CS0117`, `CS1061`, `CS1501`, `CS0111`, `CS0121`)
- `Category`: `cross-layer-contract`
- `Codes`: `CS0103, CS0117, CS1061, CS1501, CS0111, CS0121, CS0165`
- `Keywords`: `AndroidStrings.Active, AndroidStrings.Current, Math.Clamp, OneUiModalDialog, ReadableMetadataHeader, tuple, Length, Count, duplicate, partial`
- `RootCause`: Guessing member names, casing (`LFEVolume` vs `LfeVolume`, `WavChannelSkip`), namespaces (`Cavern.Neural` vs `Cavernize.Logic.Spatial`), parameter order, tuple field names, `static` vs instance calls (`OneUiModalDialog.Show`, `KaiserSincResampler`), `.Length` vs `.Count`, `AndroidStrings.Active` (never `.Current`), narrow integer `Math.Clamp` overload ambiguity, or deleting shared constants/methods without updating all callers causes cascading multi-layer build breaks.
- `TimelessRule`: NEVER call, rename, or remove a cross-layer symbol from memory -> ALWAYS grep/read the exact declaration (namespace, static/instance, parameter order, return tuple, array vs list) at its definition site && grep ALL layers (incl. all `MainActivity*.cs` partials) after any rename/removal; cast narrow integer args (`(int)`) in `Math.Clamp` w/ `int` bounds; use `AndroidStrings.Active` (never `.Current`); keep `out` vars initialized outside short-circuit chains.
- `DynamicLookup`: Grep exact symbol definition in `Cavern/`, `Cavern.Format/`, `Cavernize.Logic/`, & `CavernizeAndroid/` right before editing && run `verify_universal.py --build` when local toolchain exists.
- `AutoGuard`: `diff_rules` (`AndroidStrings.Current` fail rule) + `wiring_gate` + `check_orphan_calls` in `verify_universal.py`

### `BL-007` | Target-Framework C# Language Level Constraints (`netstandard2.1` / C# 8.0)
- `Category`: `language-compat`
- `Codes`: `CS8370, CS8400, CS8652, CS8773`
- `Keywords`: `netstandard2.1, C# 8.0, is not, file-scoped namespace, target-typed new, LangVersion`
- `RootCause`: Core libraries (`Cavern/`, `Cavern.Format/`) target `netstandard2.1` (default C# 8.0) while app projects target newer .NET TFMs; introducing newer C# syntax (`is not`, file-scoped `namespace X;`, target-typed `new()`) in core libraries fails compilation.
- `TimelessRule`: Always respect each project's `<TargetFramework>` and `<LangVersion>` before writing syntax; inside `Cavern/` & `Cavern.Format/` keep strict C# 8.0 (`!is not`, `!file-scoped namespace`, `!target-typed new()`).
- `DynamicLookup`: Inspect `<TargetFramework>` and `<LangVersion>` in the target `.csproj` before introducing newer C# language features.
- `AutoGuard`: `diff_rules` (`is not`, file-scoped `namespace`, & target-typed `new()` fail rules in `.agents/planner_profile.json`)

### `BL-008` | XML Documentation Comments & Overload Ambiguity (`CS1591`, `CS0419`)
- `Category`: `xml-docs`
- `Codes`: `CS1591, CS0419, CS1570, CS1573, CS1574`
- `Keywords`: `missing XML comment, ambiguous reference in cref, badly formed XML, GenerateDocumentationFile, summary, see cref, &amp;`
- `RootCause`: Projects w/ XML doc generation warn (`CS1591`) on undocumented `public` types/members/enums, emit `CS0419` on unqualified overloaded `<see cref="Type.Method"/>`, && emit `CS1570` on raw unescaped XML characters (`&`, `<`, `>`) inside `///` comments.
- `TimelessRule`: ALWAYS add `/// <summary>` on every new `public` type, member, & enum value in XML-doc-enabled libraries; reference overloaded methods w/ `<c>Type.Method</c>` OR full parameter-type signature in `cref`; ALWAYS escape raw `&`/`<`/`>` as `&amp;`/`&lt;`/`&gt;` inside `///` comments.
- `DynamicLookup`: Check `<GenerateDocumentationFile>` in target `.csproj` && inspect `fetch_live_diagnostics.py --ci` for `CS1591`/`CS0419`/`CS1570` regressions.
- `AutoGuard`: `diff_rules` (unescaped `&` in `///` warn rule) + CI diagnostics code breakdown (`CS1591`, `CS0419`, `CS1570`)

### `BL-009` | Unassigned Fields & Numeric Format Specifier Traps (`CS0649`, `:0.1`/`:0.2`)
- `Category`: `csharp-correctness`
- `Codes`: `CS0649, CS0414, CS0168, CS0219`
- `Keywords`: `never assigned to, format specifier, :0.1, :0.2, :0.0, :F1`
- `RootCause`: Uninitialized private fields that are read emit `CS0649`; writing `:0.1` or `:0.2` in C# numeric format strings is a silent logic bug because digits other than `0`/`#` are emitted as literal characters instead of decimal precision.
- `TimelessRule`: Explicitly initialize read fields (`= 0`) or delete dead fields; ALWAYS format fixed decimals w/ `:0.0` / `:0.00` or `:F1` / `:F2` (NEVER `:0.1` / `:0.2`).
- `DynamicLookup`: Check compiler warnings via `fetch_live_diagnostics.py --ci` && verify string interpolation formats statically pre-commit.
- `AutoGuard`: `diff_rules` (`\{[^}\n]*:0\.[1-9]\b` warn rule in `.agents/planner_profile.json`) + CI `High` priority on `CS0649`

### `BL-010` | Android Packaging Duplicates & Asset Extension Stripping (`XA4301`, `aapt2` `.gz`)
- `Category`: `android-packaging`
- `Codes`: `XA4301, XA4300, APT2000, APT2067`
- `Keywords`: `APK already contains, AndroidNativeLibrary, AndroidAsset, EnableDefaultAndroidItems, aapt2, .gz, .chrirz, .aeqz`
- `RootCause`: `.NET Android` SDK default item globs can auto-discover `NativeLibs`/`Assets`, causing `XA4301` duplicate APK item warnings when `.csproj` uses `<Include>` instead of `<Update>` (or explicit `<Remove>`); naming compressed assets `.gz` causes `aapt2` to strip/mangle the suffix.
- `TimelessRule`: Avoid duplicating SDK-default Android items in `.csproj` (prefer `Update` or inspect effective item inclusion before adding `<AndroidNativeLibrary Include=...>` / `<AndroidAsset Include=...>`); NEVER ship compressed binary assets w/ `.gz` extension (use neutral custom extensions like `.chrirz`/`.aeqz`).
- `DynamicLookup`: Verify current `.NET Android` SDK default item glob rules (`EnableDefaultAndroidItems`) in official docs && inspect `fetch_live_diagnostics.py --ci` for `XA4301`/`APT*` warnings.
- `AutoGuard`: CI diagnostics `High` priority on `XA*`/`APT*`

### `BL-011` | NDK / CMake Minimum API & Native Cross-Compilation Contracts
- `Category`: `native-ndk`
- `Codes`: `NDK_LINK, CMAKE_VAR, -Wimplicit-function-declaration, -Wunused, -Werror`
- `Keywords`: `dlsym, RTLD_DEFAULT, ANDROID_PLATFORM, LIBUSB_ROOT, HAVE_CONFIG_H, sys/time.h, reserve, URB, cmake`
- `RootCause`: Calling NDK C/C++ symbols introduced above the configured minimum `ANDROID_PLATFORM` breaks linking; fragile shell-escaped compiler `-D` defines fail across CMake/shell layers; missing POSIX headers or growing `std::vector` backing buffers after submitting async USB URBs causes compile errors or UAF crashes.
- `TimelessRule`: Resolve NDK symbols newer than minimum `ANDROID_PLATFORM` dynamically via `dlsym(RTLD_DEFAULT, ...)`; pass canonical CMake root variables + `HAVE_CONFIG_H=1` (avoid raw shell-escaped macros); include explicit POSIX headers (`<sys/time.h>`, etc.); pre-`reserve()` native transfer buffers before submitting async IO/URBs; validate host C/C++ unit tests w/ `-Wall -Wextra -Werror`.
- `DynamicLookup`: Check current NDK API availability headers & `CMakeLists.txt` minimum `ANDROID_PLATFORM` before adding native calls; verify native tests via `gcc`/`g++` & CI `00_Native_NDK`.
- `AutoGuard`: `build_rules` (`test_proaudio_native.c` C11 `-Werror`) + CI `00_Native_NDK` gate

### `BL-012` | W^X Native Binary Execution, Page-Size ELF Alignment & Dynamic FFI Headers
- `Category`: `native-security`
- `Codes`: `WX_VIOLATION, ELF_ALIGN, ORT_VTABLE`
- `Keywords`: `NativeLibraryDir, FilesDir/bin, extractNativeLibs, PT_LOAD, 16 KB, onnxruntime_c_api.h, OrtApi, NativeLibrary.Load`
- `RootCause`: Modern Android enforces W^X (blocking execution/`dlopen` from writable app storage) and stricter ELF page-size alignment; hardcoding native C struct vtable indices (e.g., ONNX Runtime `OrtApi`) breaks whenever upstream adds/reorders macros or whitespace.
- `TimelessRule`: NEVER execute or `dlopen` binaries from writable `FilesDir`/`CacheDir`; bundle native executables/libraries in APK `NativeLibs` executed from `ApplicationInfo.NativeLibraryDir` (`extractNativeLibs="true"`); verify ELF `PT_LOAD` alignment & dynamic symbol exports in CI; parse upstream C API headers dynamically w/ CI anchor assertions instead of static vtable offsets.
- `DynamicLookup`: Consult current Android NDK page-size/W^X requirements && upstream library C header layout; confirm CI `00b_NativeLibs` & APK alignment gates pass.
- `AutoGuard`: `diff_rules` (W^X `FilesDir/bin` fail rule) + CI `00b_NativeLibs` ELF/export/header anchor checks

### `BL-013` | One UI 9 Anti-Clipping, Component Parity & Localization Key Integrity
- `Category`: `ui-l10n`
- `Codes`: `L10N_PARITY, UI_CLIPPING, UI_LEGACY_WIDGET`
- `Keywords`: `BaselineAligned, CenterVertical, SetSingleLine, Ellipsize, AlertDialog, Spinner, OneUiModalDialog, OneUiInlineSelector, AndroidStrings`
- `RootCause`: Horizontal Android `LinearLayout` rows w/ buttons/pills clip vertical halves without `BaselineAligned = false`; stock `AlertDialog`/`Spinner` break One UI 9 parity; adding UI string keys in one locale or referencing typos (`S["..."]`) causes runtime fallback leaks or missing strings.
- `TimelessRule`: Every horizontal `LinearLayout` containing buttons/pills MUST set `BaselineAligned = false`, `GravityFlags.CenterVertical`, & `SetSingleLine(true)` + `Ellipsize` (or scroll container); ALWAYS use `OneUiModalDialog`, `OneUiInlineSelector`, & `OneUiIconView`; keep 100% EN/PT-BR key parity in `AndroidStrings.cs` w/ zero missing `S[...]`/`AndroidStrings.Active[...]` references.
- `DynamicLookup`: Check `.agents/docs/ONEUI9_REFERENCE.md` for component specs && run `key_parity_rules` / `verify_universal.py` on every UI/string edit.
- `AutoGuard`: `diff_rules` (`AlertDialog`, `Spinner`, `BaselineAligned`) + `key_parity_rules` (`AndroidStrings.cs` parity & ref scan)

### `BL-014` | External Tool, Archive, Stream & Neural Export Contracts
- `Category`: `tooling-io`
- `Codes`: `ARCHIVE_SIDECAR, STREAM_SEEK, ONNX_EXPORT`
- `Keywords`: `.sha256, .tar.gz, Seek(0), ExtractTrackFromContainer, export_apollo_onnx, buffer name, dynamic batch, probe exit`
- `RootCause`: Loose archive filename matching picks `.tar.gz.sha256` sidecars; container extractors fail silently when stream position != 0; neural export scripts break when upstream PyTorch/model buffer names or dynamic batch reshapes drift; probe commands return non-zero exit codes that should not be logged as fatal errors.
- `TimelessRule`: Filter archive downloads strictly by full extension excluding `.sha256`/signature sidecars; reset stream position (`Seek(0)`) before container track extraction; inspect actual upstream module/buffer names & verify numerical parity (`max_abs_diff`) when updating ONNX export scripts; handle expected non-zero probe exits cleanly.
- `DynamicLookup`: Inspect upstream release asset lists & live model module state dicts before changing downloaders or export workflows.
- `AutoGuard`: CI SHA-256 verification + ONNX export workflow parity verification

### `BL-015` | CI Workflow Hygiene, OTA Version Sync & Release Preservation
- `Category`: `release-ci`
- `Codes`: `OTA_VERSION_MISMATCH, RELEASE_OVERWRITE, SKIP_CI_MISUSE`
- `Keywords`: `ApplicationDisplayVersion, android-latest, github.run_number, [skip ci], historical release`
- `RootCause`: Divergent major.minor prefixes between `.csproj` and CI workflow break monotonic OTA update checks; overwriting old version tags destroys rollback history; missing `[skip ci]` on docs-only commits wastes CI minutes, while accidental `[skip ci]` on code commits skips required APK validation.
- `TimelessRule`: Keep `ApplicationDisplayVersion` major.minor prefix synchronized between `CavernizeAndroid.csproj` & `.github/workflows/BuildAndroid.yml`; publish unique versioned releases alongside `android-latest` (NEVER delete/overwrite prior releases); append `[skip ci]` ONLY on docs/chore-only commits && OMIT `[skip ci]` whenever code or workflow files change.
- `DynamicLookup`: Check current version prefix in `CavernizeAndroid.csproj` & `BuildAndroid.yml` (`verify_universal.py` `check_version_sync`) before bumping or releasing.
- `AutoGuard`: `verify_universal.py` `check_version_sync` + `planner_guard_command.py` `[skip ci]` pre-push tip

### `BL-016` | Pre-Commit Verification Parity & Post-Error Learning Loop
- `Category`: `process-qa`
- `Codes`: `UNVERIFIED_PUSH, EXTRA_REQUESTS_PENDING`
- `Keywords`: `check_android_cs.py, verify_universal.py, fetch_live_diagnostics.py, EXTRA_REQUESTS.md, error_learner.py`
- `RootCause`: Pushing unverified edits when static/Roslyn checks exist—or fixing a CI failure without encoding its timeless root cause—causes the exact same error classes to recur across sessions and AI models.
- `TimelessRule`: Clear `.agents/docs/EXTRA_REQUESTS.md` first; run all available local checks (`verify_universal.py`, `check_android_cs.py`, unit tests, syntax/brace/YAML/JSON checks) BEFORE committing/pushing; after ANY new structural CI or build failure is diagnosed, run `error_learner.py` to verify whether a new timeless lesson (or guardrail) should be added.
- `DynamicLookup`: Fetch latest live CI diagnostics (`python3 -B .agents/scripts/fetch_live_diagnostics.py --ci`) && check `ci/latest-learned-lessons.md` before starting follow-up fixes.
- `AutoGuard`: `planner_stop.py` + `planner_guard_command.py` + CI `error_learner.py` report

### `BL-017` | Statement-vs-Initializer Syntax & Local Roslyn CI-Parity Verification (`CS1002`, `CS1513`, `CS0019`, `CS1729`)
- `Category`: `pre-push-ci-parity`
- `Codes`: `CS1002, CS1513, CS0019, CS0246, CS1729`
- `Keywords`: `trailing comma, ApplyPortuguese, indexer assignment, check_android_cs.py --ci-parity, Roslyn analyzers, gitless container, verify_universal --all`
- `RootCause`: Pasting dictionary initializer entries (`,`) into statement blocks (`d["Key"] = "...";`), coalescing unrelated Android `View` subclasses w/o explicit `(View)` upcast (`CS0019`), or assuming git-based static checks cover gitless containers lets syntax & type errors escape to cloud CI.
- `TimelessRule`: ALWAYS terminate statement assignments w/ `;` (reserve `,` strictly for collection/object initializers); explicitly upcast heterogeneous `View` coalescing (`(View)a ?? b`); BEFORE handing off or pushing C#/C++/Android edits, ALWAYS run the full local CI-parity Roslyn + analyzer verifier (`python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings`) AND universal static checks (`verify_universal.py --all` in gitless containers).
- `DynamicLookup`: Run `python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings` (auto-fetches current portable SDK & target Android reference assembly matching `CavernizeAndroid.csproj`) && inspect `fetch_live_diagnostics.py --ci` on any remote mismatch.
- `AutoGuard`: `check_csharp_syntax_deep` + gitless `--all` in `verify_universal.py` + 6-layer `check_android_cs.py --ci-parity`

---

## 2. Dynamically Learned Lessons (Auto-Managed by `error_learner.py`)
<!-- AUTO-LEARNED-LESSONS:START -->
(No new uncatalogued structural lessons pending beyond `BL-001`..`BL-017`.)
<!-- AUTO-LEARNED-LESSONS:END -->
