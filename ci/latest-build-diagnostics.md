# Cavernize Android CI Build Diagnostics (`v2.9.77`)

- **Generated (UTC):** `2026-11-25T01:05:56Z`
- **Commit:** `2db9f84bba53c9672333154d8c050ff9e21c6523`
- **Build / Run:** `#77` (https://github.com/guumachadooo/Cavern/actions/runs/45522882927)
- **Outcome:** `compile=FAILURE` · `publish=SKIPPED`
- **Totals:** **2 unique error(s)** · **0 unique warning(s)**

## Layer Status
- **00_Native_NDK**: `SUCCESS` (errors: `0`, warnings: `0`)
- **00b_NativeLibs**: `LOGGED` (errors: `0`, warnings: `0`)
- **01_Cavern**: `PASS` (errors: `0`, warnings: `0`)
- **02_Cavern.Format**: `PASS` (errors: `0`, warnings: `0`)
- **03_Cavernize.Logic**: `PASS` (errors: `0`, warnings: `0`)
- **04_CavernizeAndroid**: `FAIL` (errors: `2`, warnings: `0`)
- **05_Shadow_FullApp**: `SKIPPED` (errors: `0`, warnings: `0`)
- **06_Publish_APK**: `SKIPPED` (errors: `0`, warnings: `0`)

## Consolidated Errors (2 unique)

### 04_CavernizeAndroid (2)
```text
CavernSamples/CavernizeAndroid/MainActivity.cs(594,16): error CS1593: Delegate 'Func<int, View>' does not take 0 arguments [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.cs(595,13): error CS1503: Argument 5: cannot convert from 'method group' to 'System.Action<int, float>' [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```

## Consolidated Warnings
Zero build warnings.
