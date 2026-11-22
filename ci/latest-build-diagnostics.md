# Cavernize Android CI Build Diagnostics (`v2.9.38`)

- **Generated (UTC):** `2026-11-22T17:59:26Z`
- **Commit:** `381fb50ba809f0a8708dfb431055cdd9e35894da`
- **Build / Run:** `#38` (https://github.com/guumachadooo/Cavern/actions/runs/45052306409)
- **Outcome:** `compile=FAILURE` · `publish=SKIPPED`
- **Totals:** **4 unique error(s)** · **35 unique warning(s)**

## Layer Status
- **00_Native_NDK**: `SUCCESS` (errors: `0`, warnings: `0`)
- **00b_NativeLibs**: `LOGGED` (errors: `0`, warnings: `0`)
- **01_Cavern**: `PASS` (errors: `0`, warnings: `0`)
- **02_Cavern.Format**: `PASS` (errors: `0`, warnings: `13`)
- **03_Cavernize.Logic**: `PASS` (errors: `0`, warnings: `0`)
- **04_CavernizeAndroid**: `FAIL` (errors: `4`, warnings: `22`)
- **05_Shadow_FullApp**: `SKIPPED` (errors: `0`, warnings: `0`)
- **06_Publish_APK**: `SKIPPED` (errors: `0`, warnings: `0`)

## Warning Breakdown by Code
| Code | Count | Priority |
| --- | ---: | --- |
| `CA1416` | 13 | High |
| `CS1591` | 11 | Low |
| `CA1422` | 8 | High |
| `CS0649` | 2 | High |
| `XA4211` | 1 | High |

## Consolidated Errors (4 unique)

### 04_CavernizeAndroid (4)
```text
CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(400,43): error CS0117: 'AndroidStrings' does not contain a definition for 'Current' [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(450,23): error CS0104: 'Stream' is an ambiguous reference between 'Android.Media.Stream' and 'System.IO.Stream' [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(777,20): error CS0103: The name 'SpatialProgramMetadata' does not exist in the current context [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(778,21): error CS0103: The name 'SoundfieldTelemetry' does not exist in the current context [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```

## Consolidated Warnings (35 unique)

### 02_Cavern.Format (13)
```text
Cavern.Format/Transcoders/EnhancedAC3Body/Memory.cs(117,13): warning CS0649: Field 'EnhancedAC3Body.lfeahtinu' is never assigned to, and will always have its default value 0 [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Transcoders/EnhancedAC3Body/Memory.cs(95,13): warning CS0649: Field 'EnhancedAC3Body.cplahtinu' is never assigned to, and will always have its default value 0 [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(15,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.CodecConfig' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(16,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.AudioElement' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(17,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.MixPresentation' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(18,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.ParameterBlock' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(19,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.TemporalDelimiter' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(20,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.AudioFrame' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(21,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.AudioFrameId0' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(22,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.AudioFrameId17' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(23,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfObuType.SequenceHeader' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(30,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfAudioElementType.ChannelBased' [Cavern.Format/Cavern.Format.csproj]
Cavern.Format/Decoders/IamfDecoder.cs(31,9): warning CS1591: Missing XML comment for publicly visible type or member 'IamfAudioElementType.SceneBased' [Cavern.Format/Cavern.Format.csproj]
```

### 04_CavernizeAndroid (22)
```text
CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml : warning XA4211: AndroidManifest.xml //uses-sdk/@android:targetSdkVersion '35' is less than $(TargetPlatformVersion) '37.0'. Using API-37.0 for ACW compilation. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Views/MediaStageViews.cs(1051,20): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'DisplayMetrics.ScaledDensity' is obsoleted on: 'Android' 34.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Views/MediaStageViews.cs(1051,65): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'DisplayMetrics.ScaledDensity' is obsoleted on: 'Android' 34.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/ApkFileProvider.cs(115,26): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'Long' is obsoleted on: 'Android' 33.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/BleOscHeadTrackerProvider.cs(290,30): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'BluetoothDevice.ConnectGatt(Context?, bool, BluetoothGattCallback?, BluetoothTransports)' is obsoleted on: 'Android' 37.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/BleOscHeadTrackerProvider.cs(441,13): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'BluetoothGattCallback.OnCharacteristicChanged(BluetoothGatt, BluetoothGattCharacteristic, byte[])' is only supported on: 'android' 33.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/MediaPlaybackNotificationManager.cs(542,56): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'ForegroundService.TypeMediaPlayback' is only supported on: 'android' 29.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/MediaPlaybackNotificationManager.cs(294,15): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'Notification.Builder' is obsoleted on: 'Android' 26.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.cs(705,13): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'Window.SetDecorFitsSystemWindows(bool)' is obsoleted on: 'android' 35.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Views/OneUiFileExplorerDialog.cs(187,45): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Settings.ActionManageAppAllFilesAccessPermission' is only supported on: 'android' 30.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.cs(1575,19): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'Intent.GetParcelableExtra(string?)' is obsoleted on: 'Android' 33.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.cs(1577,19): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Intent.GetParcelableExtra(string?, Class)' is only supported on: 'android' 33.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4921,43): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Insets.Left' is only supported on: 'android' 29.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4921,76): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Insets.Bottom' is only supported on: 'android' 29.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4921,64): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Insets.Right' is only supported on: 'android' 29.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4921,54): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Insets.Top' is only supported on: 'android' 29.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4919,52): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'WindowInsets.GetInsets(int)' is only supported on: 'android' 30.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4920,61): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'WindowInsets.Type.SystemBars()' is only supported on: 'android' 30.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4920,25): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'WindowInsets.Type.DisplayCutout()' is only supported on: 'android' 30.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4909,17): warning CA1422: This call site is reachable on: 'Android' 26.0 and later. 'Window.SetDecorFitsSystemWindows(bool)' is obsoleted on: 'android' 35.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1422) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.SettingsTab.cs(4319,37): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Settings.ActionManageAppAllFilesAccessPermission' is only supported on: 'android' 30.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/MainActivity.SettingsTab.cs(4323,37): warning CA1416: This call site is reachable on: 'Android' 26.0 and later. 'Settings.ActionManageAllFilesAccessPermission' is only supported on: 'android' 30.0 and later. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416) [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```
