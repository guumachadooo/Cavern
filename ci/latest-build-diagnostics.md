# Cavernize Android CI Build Diagnostics (`v2.9.45`)

- **Generated (UTC):** `2026-11-22T20:23:54Z`
- **Commit:** `079b7e21a1dfb6ec47187ac8af9ed10a1efce01e`
- **Build / Run:** `#45` (https://github.com/guumachadooo/Cavern/actions/runs/45066062736)
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
CavernSamples/CavernizeAndroid/Language/AndroidStrings.cs(1907,1055): error CS1002: ; expected [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Language/AndroidStrings.cs(1907,1055): error CS1513: } expected [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```

## Consolidated Warnings
Zero build warnings.
