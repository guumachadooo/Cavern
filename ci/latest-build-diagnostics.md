# Cavernize Android CI Build Diagnostics (`v2.9.39`)

- **Generated (UTC):** `2026-11-22T18:29:54Z`
- **Commit:** `50cd79e35fa0a1ee383786479ae9c25e6929fbbb`
- **Build / Run:** `#39` (https://github.com/guumachadooo/Cavern/actions/runs/45054910038)
- **Outcome:** `compile=SUCCESS` · `publish=SUCCESS`
- **Totals:** **0 unique error(s)** · **9 unique warning(s)**

## Layer Status
- **00_Native_NDK**: `SUCCESS` (errors: `0`, warnings: `0`)
- **00b_NativeLibs**: `LOGGED` (errors: `0`, warnings: `0`)
- **01_Cavern**: `PASS` (errors: `0`, warnings: `0`)
- **02_Cavern.Format**: `PASS` (errors: `0`, warnings: `0`)
- **03_Cavernize.Logic**: `PASS` (errors: `0`, warnings: `0`)
- **04_CavernizeAndroid**: `PASS` (errors: `0`, warnings: `9`)
- **05_Shadow_FullApp**: `SKIPPED` (errors: `0`, warnings: `0`)
- **06_Publish_APK**: `SUCCESS` (errors: `0`, warnings: `0`)

## Warning Breakdown by Code
| Code | Count | Priority |
| --- | ---: | --- |
| `CS0419` | 4 | Low |
| `XA4301` | 4 | High |
| `XA1006` | 1 | High |

## Consolidated Errors
Zero compilation/packaging errors.

## Consolidated Warnings (9 unique)

### 04_CavernizeAndroid (9)
```text
CavernSamples/CavernizeAndroid/Services/MediaServiceUtils.cs(65,121): warning CS0419: Ambiguous reference in cref attribute: 'TimeSpan.TryParse'. Assuming 'System.TimeSpan.TryParse(System.ReadOnlySpan<char>, System.IFormatProvider?, out System.TimeSpan)', but could have also matched other overloads including 'System.TimeSpan.TryParse(System.ReadOnlySpan<char>, out System.TimeSpan)'. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/NativeOnnxRuntimeBridge.cs(491,51): warning CS0419: Ambiguous reference in cref attribute: 'NativeLibrary.Load'. Assuming 'System.Runtime.InteropServices.NativeLibrary.Load(string)', but could have also matched other overloads including 'System.Runtime.InteropServices.NativeLibrary.Load(string, System.Reflection.Assembly, System.Runtime.InteropServices.DllImportSearchPath?)'. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/UsbProAudioDeviceManager.cs(228,23): warning CS0419: Ambiguous reference in cref attribute: 'UsbDeviceConnection.ControlTransfer'. Assuming 'Android.Hardware.Usb.UsbDeviceConnection.ControlTransfer(Android.Hardware.Usb.UsbAddressing, int, int, int, byte[]?, int, int, int)', but could have also matched other overloads including 'Android.Hardware.Usb.UsbDeviceConnection.ControlTransfer(Android.Hardware.Usb.UsbAddressing, int, int, int, byte[]?, int, int)'. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
CavernSamples/CavernizeAndroid/Services/UsbProAudioDeviceManager.cs(728,71): warning CS0419: Ambiguous reference in cref attribute: 'UsbDeviceConnection.ControlTransfer'. Assuming 'Android.Hardware.Usb.UsbDeviceConnection.ControlTransfer(Android.Hardware.Usb.UsbAddressing, int, int, int, byte[]?, int, int, int)', but could have also matched other overloads including 'Android.Hardware.Usb.UsbDeviceConnection.ControlTransfer(Android.Hardware.Usb.UsbAddressing, int, int, int, byte[]?, int, int)'. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
/usr/share/dotnet/packs/Microsoft.Android.Sdk.Linux/37.2.31/targets/Microsoft.Android.Sdk.Tooling.targets(72,5): warning XA1006: The TargetFrameworkVersion (Android API level 37) is higher than the targetSdkVersion (35). Please increase the `android:targetSdkVersion` in the `AndroidManifest.xml` so that the API levels match. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
NativeLibs/arm64-v8a/libcavernize_proaudio.so : warning XA4301: APK already contains the item lib/arm64-v8a/libcavernize_proaudio.so; ignoring. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
NativeLibs/arm64-v8a/libffmpeg.so : warning XA4301: APK already contains the item lib/arm64-v8a/libffmpeg.so; ignoring. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
NativeLibs/arm64-v8a/libonnxruntime.so : warning XA4301: APK already contains the item lib/arm64-v8a/libonnxruntime.so; ignoring. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
NativeLibs/arm64-v8a/libtruehdd.so : warning XA4301: APK already contains the item lib/arm64-v8a/libtruehdd.so; ignoring. [CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj]
```
