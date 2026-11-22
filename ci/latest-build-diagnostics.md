# Cavernize Android CI Build Diagnostics (`v2.9.42`)

- **Generated (UTC):** `2026-11-22T19:30:57Z`
- **Commit:** `ec76e8136c07d6aebad47a0bd7a36ae60ba0c72c`
- **Build / Run:** `#42` (https://github.com/guumachadooo/Cavern/actions/runs/45060946806)
- **Outcome:** `compile=FAILURE` · `publish=SKIPPED`
- **Totals:** **2 unique error(s)** · **1 unique warning(s)**

## Layer Status
- **00_Native_NDK**: `SUCCESS` (errors: `0`, warnings: `0`)
- **00b_NativeLibs**: `LOGGED` (errors: `0`, warnings: `0`)
- **01_Cavern**: `PASS` (errors: `0`, warnings: `0`)
- **02_Cavern.Format**: `PASS` (errors: `0`, warnings: `0`)
- **03_Cavernize.Logic**: `PASS` (errors: `0`, warnings: `0`)
- **04_CavernizeAndroid**: `FAIL` (errors: `2`, warnings: `1`)
- **05_Shadow_FullApp**: `SKIPPED` (errors: `0`, warnings: `0`)
- **06_Publish_APK**: `SKIPPED` (errors: `0`, warnings: `0`)

## Warning Breakdown by Code
| Code | Count | Priority |
| --- | ---: | --- |
| `CS1570` | 1 | Low |

## Consolidated Errors (2 unique)

### 04_CavernizeAndroid (2)
```text
CavernSamples/CavernizeAndroid/Services/LiveBridgeAuthActivity.cs(137,13): error CS0117: 'Button' does not contain a definition for 'AllCaps' [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/LiveBridgeAuthActivity.cs(295,97): error CS0117: 'BiometricErrorCode' does not contain a definition for 'NegativeButton' [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```

## Consolidated Warnings (1 unique)

### 04_CavernizeAndroid (1)
```text
CavernSamples/CavernizeAndroid/Services/AppSettingsStore.cs(1359,65): warning CS1570: XML comment has badly formed XML -- 'Whitespace is not allowed at this location.' [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```
