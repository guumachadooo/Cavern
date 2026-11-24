# Cavernize Android CI Build Diagnostics (`v2.9.67`)

- **Generated (UTC):** `2026-11-24T17:28:21Z`
- **Commit:** `812279f719dcdb13c08ac8140844c11b729487db`
- **Build / Run:** `#67` (https://github.com/guumachadooo/Cavern/actions/runs/45447893057)
- **Outcome:** `compile=SUCCESS` · `publish=SUCCESS`
- **Totals:** **0 unique error(s)** · **2 unique warning(s)**

## Layer Status
- **00_Native_NDK**: `SUCCESS` (errors: `0`, warnings: `0`)
- **00b_NativeLibs**: `LOGGED` (errors: `0`, warnings: `0`)
- **01_Cavern**: `PASS` (errors: `0`, warnings: `0`)
- **02_Cavern.Format**: `PASS` (errors: `0`, warnings: `0`)
- **03_Cavernize.Logic**: `PASS` (errors: `0`, warnings: `0`)
- **04_CavernizeAndroid**: `PASS` (errors: `0`, warnings: `2`)
- **05_Shadow_FullApp**: `SKIPPED` (errors: `0`, warnings: `0`)
- **06_Publish_APK**: `SUCCESS` (errors: `0`, warnings: `0`)

## Warning Breakdown by Code
| Code | Count | Priority |
| --- | ---: | --- |
| `CA1416` | 2 | High |

## Consolidated Errors
Zero compilation/packaging errors.

## Consolidated Warnings (2 unique)

### 04_CavernizeAndroid (2)
```text
CavernSamples/CavernizeAndroid/Services/ExportMediaProcessingService.cs(439,39): warning CA1416: This call site is reachable on: 'android' 34.0 and later. 'ForegroundService.TypeMediaProcessing' is only supported on: 'android' 35.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/ExportMediaProcessingService.cs(473,51): warning CA1416: This call site is reachable on: 'Android' 34.0 and later. 'ForegroundService.TypeMediaProcessing' is only supported on: 'android' 35.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```
