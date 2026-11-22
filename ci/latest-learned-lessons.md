# Dynamic Build Error Learner Report

- **Source:** `build-diagnostics.json`
- **Matched Existing Lessons:** `0` diagnostic(s) across `0` lesson(s)
- **Skipped Transient/Noise:** `0`
- **New Timeless Candidates:** `2`

## New Timeless Lesson Candidates (Ready for `--apply`)
Run `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` to merge into `.agents/docs/BUILD_LESSONS.md`.

### `BL-017` | Auto-Learned CS Diagnostic `CS1002`
- `Category`: `csharp-compiler`
- `Codes`: `CS1002`
- `Keywords`: `cs1002, csharp-compiler`
- `RootCause`: Recurring error `CS1002` (04_CavernizeAndroid): ; expected
- `TimelessRule`: Resolve `CS1002` at root cause: verify exact types, overloads, nullability, language level of target `.csproj`, & namespace qualifications instead of guessing signatures.
- `DynamicLookup`: Inspect exact symbol declarations in repo + current C# compiler reference for `CS1002` && validate via local build / CI diagnostics.
- `AutoGuard`: CI diagnostics (`CS1002`) -> evaluate adding `diff_rule` if recurring

### `BL-018` | Auto-Learned CS Diagnostic `CS1513`
- `Category`: `csharp-compiler`
- `Codes`: `CS1513`
- `Keywords`: `cs1513, csharp-compiler`
- `RootCause`: Recurring error `CS1513` (04_CavernizeAndroid): } expected
- `TimelessRule`: Resolve `CS1513` at root cause: verify exact types, overloads, nullability, language level of target `.csproj`, & namespace qualifications instead of guessing signatures.
- `DynamicLookup`: Inspect exact symbol declarations in repo + current C# compiler reference for `CS1513` && validate via local build / CI diagnostics.
- `AutoGuard`: CI diagnostics (`CS1513`) -> evaluate adding `diff_rule` if recurring
