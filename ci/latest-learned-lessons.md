# Dynamic Build Error Learner Report

- **Source:** `build-diagnostics.json`
- **Matched Existing Lessons:** `1` diagnostic(s) across `1` lesson(s)
- **Skipped Transient/Noise:** `0`
- **New Timeless Candidates:** `1`

## Matched Existing Lessons (Already Catalogued)
- `BL-005`: 1 occurrence(s) (`CS1503`)

## New Timeless Lesson Candidates (Ready for `--apply`)
Run `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` to merge into `.agents/docs/BUILD_LESSONS.md`.

### `BL-018` | Auto-Learned CS Diagnostic `CS1593`
- `Category`: `csharp-compiler`
- `Codes`: `CS1593`
- `Keywords`: `cs1593, csharp-compiler`
- `RootCause`: Recurring error `CS1593` (04_CavernizeAndroid): Delegate 'Func<int, View>' does not take 0 arguments
- `TimelessRule`: Resolve `CS1593` at root cause: verify exact types, overloads, nullability, language level of target `.csproj`, & namespace qualifications instead of guessing signatures.
- `DynamicLookup`: Inspect exact symbol declarations in repo + current C# compiler reference for `CS1593` && validate via local build / CI diagnostics.
- `AutoGuard`: CI diagnostics (`CS1593`) -> evaluate adding `diff_rule` if recurring
