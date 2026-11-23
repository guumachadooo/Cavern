using System.Collections.Specialized;
using System.Globalization;
using System.Numerics;

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.ProAudio;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavern.Waveforms;

using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using VoidX.WPF.FFmpeg;
using VoidX.WPF.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    void BuildSettingsSpatialCards() {
        // Physical Externalization (Allen & Berkley ISM Room, Duda & Martens Near-Field DVF & ISO 9613-1)
        LinearLayout slot5aCard = Card(S["Slot5ACardTitle"], OneUiIconType.Speaker, OneUiTheme.IconSpatial);
        ismRoomEnabledSwitch = AddSwitchRow(slot5aCard, S["IsmRoomEnableTitle"], store.IsmRoomEnabled, v => {
            store.IsmRoomEnabled = v;
            ApplyAndRefreshExternalization();
        }, helpBody: S["TipIsmRoom"]);

        externalizationStatusText = Label("", 11.4f, OneUiTheme.TextSub);
        externalizationStatusText.SetSingleLine(true);
        externalizationStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        slot5aCard.AddView(externalizationStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(8)
        });

        ismRoomPresetSelector = new OneUiInlineSelector(this, S["IsmRoomPresetTitle"]);
        string[] ismPresetLabels = [
            S["IsmPresetMastering"],
            S["IsmPresetMixing"],
            S["IsmPresetLiving"],
            S["IsmPresetCinema"],
            S["IsmPresetCustom"]
        ];
        ismRoomPresetSelector.SetItems(ismPresetLabels, Math.Clamp(store.IsmRoomPresetIndex, 0, ismPresetLabels.Length - 1));
        ismRoomPresetSelector.SelectionChanged += pos => {
            store.IsmRoomPresetIndex = pos;
            ApplyAndRefreshExternalization();
        };
        slot5aCard.AddView(ismRoomPresetSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        ismWallMaterialSelector = new OneUiInlineSelector(this, S["IsmWallMaterialTitle"]);
        string[] ismMaterialLabels = [
            S["IsmMatBroadband"],
            S["IsmMatTreated"],
            S["IsmMatPlaster"],
            S["IsmMatCinema"],
            S["IsmMatWood"]
        ];
        ismWallMaterialSelector.SetItems(ismMaterialLabels, Math.Clamp(store.IsmWallMaterialIndex, 0, ismMaterialLabels.Length - 1));
        ismWallMaterialSelector.SelectionChanged += pos => {
            store.IsmWallMaterialIndex = pos;
            ApplyAndRefreshExternalization();
        };
        slot5aCard.AddView(ismWallMaterialSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        ismOrderSelector = new OneUiInlineSelector(this, S["IsmOrderTitle"]);
        string[] ismOrderLabels = [S["IsmOrder1"], S["IsmOrder2"]];
        ismOrderSelector.SetItems(ismOrderLabels, store.IsmReflectionOrder <= 1 ? 0 : 1);
        ismOrderSelector.SelectionChanged += pos => {
            store.IsmReflectionOrder = pos == 0 ? 1 : 2;
            ApplyAndRefreshExternalization();
        };
        slot5aCard.AddView(ismOrderSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        (ismLevelBar, _) = AddSliderRow(
            slot5aCard,
            S["IsmLevelTitle"],
            0,
            150,
            Math.Clamp((int)Math.Round(store.IsmReflectionLevel * 100f), 0, 150),
            v => {
                store.IsmReflectionLevel = v * 0.01f;
                ApplyAndRefreshExternalization();
            },
            "%",
            defaultValue: 100);

        AddDivider(slot5aCard);
        nearFieldEnabledSwitch = AddSwitchRow(slot5aCard, S["NearFieldEnableTitle"], store.NearFieldCompensationEnabled, v => {
            store.NearFieldCompensationEnabled = v;
            ApplyAndRefreshExternalization();
        }, helpBody: S["TipNearField"]);

        AddDivider(slot5aCard);
        airAbsorptionEnabledSwitch = AddSwitchRow(slot5aCard, S["AirAbsEnableTitle"], store.AirAbsorptionEnabled, v => {
            store.AirAbsorptionEnabled = v;
            ApplyAndRefreshExternalization();
        }, helpBody: S["TipAirAbsorption"]);

        (airTempBar, _) = AddSliderRow(
            slot5aCard,
            S["AirTempTitle"],
            0,
            40,
            Math.Clamp((int)Math.Round(store.AirTemperatureCelsius), 0, 40),
            v => {
                store.AirTemperatureCelsius = v;
                ApplyAndRefreshExternalization();
            },
            " °C",
            defaultValue: 20);

        (airHumidityBar, _) = AddSliderRow(
            slot5aCard,
            S["AirHumidityTitle"],
            15,
            90,
            Math.Clamp((int)Math.Round(store.AirRelativeHumidityPct), 15, 90),
            v => {
                store.AirRelativeHumidityPct = v;
                ApplyAndRefreshExternalization();
            },
            "%",
            defaultValue: 50);

        RegisterSettingsCard(slot5aCard, categoryIndex: 1, isProOnly: true, "physical externalization ism room reflections near field dvf air absorption temperature humidity");

        // MagLS Ambisonics, Woodworth-Schlosberg Biometric ITD & HpTF Ear-Canal Notch
        LinearLayout slot5bCard = Card(S["Slot5BCardTitle"], OneUiIconType.Headphones, OneUiTheme.IconSpatial);

        personalizationStatusText = Label("", 11.4f, OneUiTheme.TextSub);
        personalizationStatusText.SetSingleLine(true);
        personalizationStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        slot5bCard.AddView(personalizationStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        magLsModeSelector = new OneUiInlineSelector(this, S["MagLsModeTitle"]);
        string[] magLsLabels = [
            S["MagLsModeOff"],
            S["MagLsModeO1"],
            S["MagLsModeO2"],
            S["MagLsModeO3"]
        ];
        magLsModeSelector.SetItems(magLsLabels, Math.Clamp(store.MagLsModeIndex, 0, magLsLabels.Length - 1));
        magLsModeSelector.SelectionChanged += pos => {
            store.MagLsModeIndex = pos;
            ApplyAndRefreshPersonalization(rebuildHrir: true);
        };
        slot5bCard.AddView(magLsModeSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        magLsCutoffSelector = new OneUiInlineSelector(this, S["MagLsCutoffTitle"]);
        string[] magLsCutoffLabels = [S["MagLsCutoffAuto"], "1.5 kHz", "2.5 kHz", "3.5 kHz", "5.0 kHz"];
        magLsCutoffSelector.SetItems(magLsCutoffLabels, ResolveMagLsCutoffIndex(store.MagLsCutoffHz));
        magLsCutoffSelector.SelectionChanged += pos => {
            int safe = Math.Clamp(pos, 0, MagLsCutoffPresets.Length - 1);
            store.MagLsCutoffHz = MagLsCutoffPresets[safe];
            ApplyAndRefreshPersonalization(rebuildHrir: true);
        };
        slot5bCard.AddView(magLsCutoffSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddDivider(slot5bCard);
        biometricItdSwitch = AddSwitchRow(slot5bCard, S["BiometricItdEnableTitle"], store.BiometricItdEnabled, v => {
            store.BiometricItdEnabled = v;
            ApplyAndRefreshPersonalization(rebuildHrir: true);
        }, helpBody: S["TipBiometricItd"]);

        (headWidthBar, _) = AddSliderRow(
            slot5bCard,
            S["HeadWidthTitle"],
            130,
            180,
            Math.Clamp((int)Math.Round(store.HeadWidthCm * 10f), 130, 180),
            v => {
                store.HeadWidthCm = v * 0.1f;
                if (headWidthBar != null) {
                    headWidthBar.ValueText = FormatHeadWidthLabel(store.HeadWidthCm, store.ManualItdOffsetUs);
                    headWidthBar.Invalidate();
                }
                ApplyAndRefreshPersonalization(rebuildHrir: store.BiometricItdEnabled);
            },
            " mm",
            defaultValue: 155,
            helpBody: S["TipHeadWidth"]);
        if (headWidthBar != null) {
            headWidthBar.ValueText = FormatHeadWidthLabel(store.HeadWidthCm, store.ManualItdOffsetUs);
        }

        (manualItdBar, _) = AddSliderRow(
            slot5bCard,
            S["ManualItdOffsetTitle"],
            -120,
            120,
            Math.Clamp((int)Math.Round(store.ManualItdOffsetUs), -120, 120),
            v => {
                store.ManualItdOffsetUs = v;
                if (headWidthBar != null) {
                    headWidthBar.ValueText = FormatHeadWidthLabel(store.HeadWidthCm, store.ManualItdOffsetUs);
                    headWidthBar.Invalidate();
                }
                ApplyAndRefreshPersonalization(rebuildHrir: store.BiometricItdEnabled);
            },
            " µs",
            defaultValue: 0);

        AddDivider(slot5bCard);
        hptfNotchSwitch = AddSwitchRow(slot5bCard, S["HptfNotchEnableTitle"], store.HptfNotchEnabled, v => {
            store.HptfNotchEnabled = v;
            ApplyAndRefreshPersonalization(rebuildHrir: false);
        }, helpBody: S["TipHptfNotch"]);

        (hptfFreqBar, _) = AddSliderRow(
            slot5bCard,
            S["HptfFreqTitle"],
            6000,
            10000,
            Math.Clamp((int)Math.Round(store.HptfNotchFreqHz), 6000, 10000),
            v => {
                store.HptfNotchFreqHz = v;
                ApplyAndRefreshPersonalization(rebuildHrir: false);
            },
            " Hz",
            defaultValue: 8000);

        (hptfGainBar, _) = AddSliderRow(
            slot5bCard,
            S["HptfGainTitle"],
            -12,
            0,
            Math.Clamp((int)Math.Round(store.HptfNotchGainDb), -12, 0),
            v => {
                store.HptfNotchGainDb = v;
                ApplyAndRefreshPersonalization(rebuildHrir: false);
            },
            " dB",
            defaultValue: -4);

        (hptfQBar, _) = AddSliderRow(
            slot5bCard,
            S["HptfQTitle"],
            3,
            16,
            Math.Clamp((int)Math.Round(store.HptfNotchQ), 3, 16),
            v => {
                store.HptfNotchQ = v;
                ApplyAndRefreshPersonalization(rebuildHrir: false);
            },
            "",
            defaultValue: 8);

        LinearLayout slot5bRow1 = HRow();
        slot5bRow1.SetPadding(0, Dp(6), 0, 0);
        Button wizardBtn = PrimaryButton(S["HrtfWizLaunchBtn"], ShowPerceptualHrtfWizardDialog);
        Button toneBtn = PillButton(S["HptfToneTestBtn"], () => PlayHptfTestToneAsync(sweep: false));
        slot5bRow1.AddView(wizardBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f) { RightMargin = Dp(8) });
        slot5bRow1.AddView(toneBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        slot5bCard.AddView(slot5bRow1, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        LinearLayout slot5bRow2 = HRow();
        slot5bRow2.SetPadding(0, Dp(8), 0, 0);
        Button sweepBtn = PillButton(S["HptfSweepTestBtn"], () => PlayHptfTestToneAsync(sweep: true));
        Button import5bBtn = PillButton(S["Slot5BImportBtn"], LaunchCustomHrirImporter);
        slot5bRow2.AddView(sweepBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        slot5bRow2.AddView(import5bBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f));
        slot5bCard.AddView(slot5bRow2, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        RegisterSettingsCard(slot5bCard, categoryIndex: 1, isProOnly: true, "hrtf personalization magls ambisonics biometric itd head width hptf notch wizard");

    }

    void ApplyAndRefreshExternalization() {
        if (store == null || playerEngine == null) {
            return;
        }
        playerEngine.ApplyExternalizationConfig(store.BuildExternalizationConfig());
        RefreshExternalizationUi();
    }

    void RefreshExternalizationUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (ismRoomEnabledSwitch != null && ismRoomEnabledSwitch.Checked != store.IsmRoomEnabled) {
            ismRoomEnabledSwitch.Checked = store.IsmRoomEnabled;
        }
        if (nearFieldEnabledSwitch != null && nearFieldEnabledSwitch.Checked != store.NearFieldCompensationEnabled) {
            nearFieldEnabledSwitch.Checked = store.NearFieldCompensationEnabled;
        }
        if (airAbsorptionEnabledSwitch != null && airAbsorptionEnabledSwitch.Checked != store.AirAbsorptionEnabled) {
            airAbsorptionEnabledSwitch.Checked = store.AirAbsorptionEnabled;
        }
        if (ismRoomPresetSelector != null) {
            int pIdx = Math.Clamp(store.IsmRoomPresetIndex, 0, 4);
            if (ismRoomPresetSelector.SelectedIndex != pIdx) {
                ismRoomPresetSelector.SetSelection(pIdx, notify: false);
            }
        }
        if (ismWallMaterialSelector != null) {
            int mIdx = Math.Clamp(store.IsmWallMaterialIndex, 0, 4);
            if (ismWallMaterialSelector.SelectedIndex != mIdx) {
                ismWallMaterialSelector.SetSelection(mIdx, notify: false);
            }
        }
        if (ismOrderSelector != null) {
            int oIdx = store.IsmReflectionOrder <= 1 ? 0 : 1;
            if (ismOrderSelector.SelectedIndex != oIdx) {
                ismOrderSelector.SetSelection(oIdx, notify: false);
            }
        }
        SyncSlider(ismLevelBar, (int)Math.Round(store.IsmReflectionLevel * 100f), 0, 150, "%");
        SyncSlider(airTempBar, (int)Math.Round(store.AirTemperatureCelsius), 0, 40, " °C");
        SyncSlider(airHumidityBar, (int)Math.Round(store.AirRelativeHumidityPct), 15, 90, "%");
        if (externalizationStatusText != null) {
            var diag = playerEngine?.ExternalizationDiagnostics ?? Cavernize.Logic.Spatial.PhysicalExternalizationDiagnostics.Default;
            bool isBrir = BinauralProfileService.IsActiveProfileBrir(store);
            if (!store.HeadphoneVirtualizer) {
                externalizationStatusText.Text = S["ExtStatusDirect"];
                externalizationStatusText.SetTextColor(OneUiTheme.TextMuted);
            } else if (isBrir) {
                externalizationStatusText.Text = S["ExtStatusBrirGuard"];
                externalizationStatusText.SetTextColor(OneUiTheme.PeachText);
            } else if (store.IsmRoomEnabled) {
                int reflCount = store.IsmReflectionOrder <= 1
                    ? IsmRoomEngine.Order1ReflectionCount
                    : IsmRoomEngine.Order2ReflectionCount;
                externalizationStatusText.Text = string.Format(
                    S["ExtStatusActive"],
                    reflCount,
                    diag.SabineRt60Seconds,
                    diag.EyringRt60Seconds,
                    diag.RoomDimensionsMeters.X,
                    diag.RoomDimensionsMeters.Y,
                    diag.RoomDimensionsMeters.Z);
                externalizationStatusText.SetTextColor(OneUiTheme.PrimaryHigh);
            } else {
                externalizationStatusText.Text = string.Format(
                    S["ExtStatusOff"],
                    diag.SabineRt60Seconds,
                    diag.EyringRt60Seconds,
                    diag.RoomDimensionsMeters.X,
                    diag.RoomDimensionsMeters.Y,
                    diag.RoomDimensionsMeters.Z);
                externalizationStatusText.SetTextColor(OneUiTheme.TextSub);
            }
        }
    }

    static string FormatHeadWidthLabel(float headWidthCm, float manualOffsetUs) {
        double itdUs = BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, headWidthCm) + manualOffsetUs;
        return $"{headWidthCm:0.0} cm ({itdUs:0} µs)";
    }

    void ApplyAndRefreshPersonalization(bool rebuildHrir) {
        if (store == null || playerEngine == null) {
            return;
        }
        playerEngine.ApplyPersonalizationConfig(store.BuildPersonalizationConfig());
        if (!rebuildHrir) {
            BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
            RefreshPersonalizationUi();
            return;
        }

        int seq = Interlocked.Increment(ref personalizationRebuildSeq);
        _ = Task.Run(async () => {
            await Task.Delay(80);
            if (seq != Volatile.Read(ref personalizationRebuildSeq) || uiDestroyed) {
                return;
            }
            playerEngine?.RequestSmoothTransition();
            bool ok = BinauralProfileService.ApplyPersonalization(this, store, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) {
                    return;
                }
                if (ok) {
                    if (playerEngine != null) {
                        BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                    }
                    RefreshBinauralProfileUi();
                    RefreshPersonalizationUi();
                    RefreshMusicPlayerControlsUi();
                    RefreshSettingsCategorySubtitles();
                } else if (!string.IsNullOrEmpty(err)) {
                    ShowError(err);
                }
            });
        });
    }

    static int ResolveMagLsCutoffIndex(float cutoffHz) {
        if (cutoffHz <= 0f) {
            return 0;
        }
        int bestIdx = 1;
        float bestDiff = float.MaxValue;
        for (int i = 1; i < MagLsCutoffPresets.Length; i++) {
            float diff = MathF.Abs(cutoffHz - MagLsCutoffPresets[i]);
            if (diff < bestDiff) {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    void RefreshPersonalizationUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (magLsModeSelector != null) {
            int mIdx = Math.Clamp(store.MagLsModeIndex, 0, 3);
            if (magLsModeSelector.SelectedIndex != mIdx) {
                magLsModeSelector.SetSelection(mIdx, notify: false);
            }
        }
        if (magLsCutoffSelector != null) {
            int cIdx = ResolveMagLsCutoffIndex(store.MagLsCutoffHz);
            if (magLsCutoffSelector.SelectedIndex != cIdx) {
                magLsCutoffSelector.SetSelection(cIdx, notify: false);
            }
        }
        if (biometricItdSwitch != null && biometricItdSwitch.Checked != store.BiometricItdEnabled) {
            biometricItdSwitch.Checked = store.BiometricItdEnabled;
        }
        if (headWidthBar != null) {
            int mm = Math.Clamp((int)Math.Round(store.HeadWidthCm * 10f), 130, 180);
            if (headWidthBar.Progress != mm - 130) {
                headWidthBar.Progress = mm - 130;
            }
            headWidthBar.ValueText = FormatHeadWidthLabel(store.HeadWidthCm, store.ManualItdOffsetUs);
            headWidthBar.Invalidate();
        }
        if (manualItdBar != null) {
            int offsetUs = Math.Clamp((int)Math.Round(store.ManualItdOffsetUs), -120, 120);
            if (manualItdBar.Progress != offsetUs + 120) {
                manualItdBar.Progress = offsetUs + 120;
            }
        }
        if (hptfNotchSwitch != null && hptfNotchSwitch.Checked != store.HptfNotchEnabled) {
            hptfNotchSwitch.Checked = store.HptfNotchEnabled;
        }
        if (hptfFreqBar != null) {
            int hz = Math.Clamp((int)Math.Round(store.HptfNotchFreqHz), 6000, 10000);
            if (hptfFreqBar.Progress != hz - 6000) {
                hptfFreqBar.Progress = hz - 6000;
            }
        }
        if (hptfGainBar != null) {
            int db = Math.Clamp((int)Math.Round(store.HptfNotchGainDb), -12, 0);
            if (hptfGainBar.Progress != db + 12) {
                hptfGainBar.Progress = db + 12;
            }
        }
        if (hptfQBar != null) {
            int q = Math.Clamp((int)Math.Round(store.HptfNotchQ), 3, 16);
            if (hptfQBar.Progress != q - 3) {
                hptfQBar.Progress = q - 3;
            }
        }
        if (personalizationStatusText != null) {
            var diag = BinauralProfileService.BuildMergedDiagnostics(store);
            if (!store.HeadphoneVirtualizer) {
                personalizationStatusText.Text = S["PersStatusDirect"];
                personalizationStatusText.SetTextColor(OneUiTheme.TextMuted);
            } else if (diag.IsBrirBypassed) {
                personalizationStatusText.Text = S["PersStatusBrirGuard"];
                personalizationStatusText.SetTextColor(OneUiTheme.PeachText);
            } else {
                string magPart = diag.IsMagLsActive
                    ? $"MagLS O{diag.MagLsOrder} ({diag.MagLsCutoffHz:0} Hz, {diag.MagLsHfErrorDb:0.00} dB)"
                    : S["MagLsDirectHrirTag"];
                string itdPart = diag.IsBiometricItdActive
                    ? $"ITD {diag.MaxLateralItdUs:0} µs ({diag.ItdDeltaAt90DegUs:+0;-0;0})"
                    : $"ITD Ref ({diag.MaxLateralItdUs:0} µs)";
                string hptfPart = diag.IsHptfNotchActive
                    ? $"HpTF {diag.HptfNotchFreqHz * 0.001f:0.0}k {diag.HptfNotchGainDb:0.#} dB"
                    : S["HptfOffTag"];
                personalizationStatusText.Text = string.Format(S["PersStatusSummary"], magPart, itdPart, hptfPart);
                bool anyActive = diag.IsMagLsActive || diag.IsBiometricItdActive || diag.IsHptfNotchActive;
                personalizationStatusText.SetTextColor(anyActive ? OneUiTheme.PrimaryHigh : OneUiTheme.TextSub);
            }
        }
    }

    void ShowPerceptualHrtfWizardDialog() {
        if (store == null) {
            return;
        }
        // Snapshot original HRTF settings so Cancel / Back / outside tap reverts cleanly.
        string origProfileId = store.BinauralProfileId;
        bool origHeadphoneVirtualizer = store.HeadphoneVirtualizer;
        string origHrirPath = store.HrirPath;
        float origHeadWidthCm = store.HeadWidthCm;
        bool origBiometricItd = store.BiometricItdEnabled;
        float origManualItdOffsetUs = store.ManualItdOffsetUs;
        bool origMinPhase = store.MinimumPhaseHrir;
        int origMagLsOrder = store.MagLsModeIndex;
        float origMagLsCutoffHz = store.MagLsCutoffHz;
        string origClusterId = store.PerceptualClusterId;
        bool origHptfEnabled = store.HptfNotchEnabled;
        float origHptfFreqHz = store.HptfNotchFreqHz;
        float origHptfGainDb = store.HptfNotchGainDb;
        float origHptfQ = store.HptfNotchQ;

        int currentStage = 0;
        string chosenProfileId = BinauralProfileService.ResolveStoredHrtfProfileId(store);
        if (chosenProfileId != "sadie_d1_dfc" && chosenProfileId != "sadie_d2_dfc" && chosenProfileId != "thk_ku100_ff_dfc") {
            chosenProfileId = "sadie_d1_dfc";
        }
        float chosenHeadWidthCm = store.HeadWidthCm;
        bool chosenMinPhase = store.MinimumPhaseHrir;
        int chosenMagLsOrder = store.MagLsModeIndex;
        string chosenClusterId = string.IsNullOrEmpty(store.PerceptualClusterId) ? "cluster_ku100_sadie" : store.PerceptualClusterId;

        bool committed = false;
        bool reverted = false;
        bool hasPreviewed = false;
        object draftSync = new();

        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        TextView stageHeader = Label("", 13.5f, OneUiTheme.PrimaryHigh);
        stageHeader.Typeface = OneUiTheme.GetAppleFontBold(this);
        TextView stageSub = Label("", 11.6f, OneUiTheme.TextSub);
        body.AddView(stageHeader);
        body.AddView(stageSub, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(3),
            BottomMargin = Dp(10)
        });

        LinearLayout optionsHost = new(this) { Orientation = Orientation.Vertical };
        body.AddView(optionsHost);

        Dialog activeDlg = null;

        void ApplyLivePreview() {
            lock (draftSync) {
                if (reverted) {
                    return;
                }
                hasPreviewed = true;
                store.HeadWidthCm = chosenHeadWidthCm;
                store.BiometricItdEnabled = Math.Abs(chosenHeadWidthCm - BiometricItdScaler.ReferenceHeadWidthCm) >= 0.05f ||
                    Math.Abs(store.ManualItdOffsetUs) >= 1f;
                store.MinimumPhaseHrir = chosenMinPhase;
                store.MagLsModeIndex = chosenMagLsOrder;
                store.PerceptualClusterId = chosenClusterId;
            }
            ApplyBinauralProfileAsync(chosenProfileId, showToast: false);
        }

        void RevertDraftIfNeeded() {
            bool shouldRestore;
            string restoreId;
            lock (draftSync) {
                if (committed || reverted) {
                    return;
                }
                reverted = true;
                shouldRestore = hasPreviewed;
                if (shouldRestore) {
                    store.BinauralProfileId = origProfileId;
                    store.HeadphoneVirtualizer = origHeadphoneVirtualizer;
                    store.HrirPath = origHrirPath;
                    store.HeadWidthCm = origHeadWidthCm;
                    store.BiometricItdEnabled = origBiometricItd;
                    store.ManualItdOffsetUs = origManualItdOffsetUs;
                    store.MinimumPhaseHrir = origMinPhase;
                    store.MagLsModeIndex = origMagLsOrder;
                    store.MagLsCutoffHz = origMagLsCutoffHz;
                    store.PerceptualClusterId = origClusterId;
                    store.HptfNotchEnabled = origHptfEnabled;
                    store.HptfNotchFreqHz = origHptfFreqHz;
                    store.HptfNotchGainDb = origHptfGainDb;
                    store.HptfNotchQ = origHptfQ;
                }
                restoreId = origHeadphoneVirtualizer
                    ? BinauralProfileService.ResolveStoredHrtfProfileId(store)
                    : BinauralProfileService.Direct20Id;
            }
            if (shouldRestore) {
                ApplyBinauralProfileAsync(restoreId, showToast: false);
                RefreshPersonalizationUi();
            }
        }

        void RenderStage() {
            optionsHost.RemoveAllViews();
            IReadOnlyList<Cavernize.Logic.Spatial.PerceptualWizardCandidate> candidates;
            if (currentStage == 0) {
                stageHeader.Text = S["HrtfWizStage1Title"];
                stageSub.Text = S["HrtfWizStage1Sub"];
                candidates = Cavernize.Logic.Spatial.PerceptualHrtfWizardCatalog.Stage1PinnaClusters;
            } else if (currentStage == 1) {
                stageHeader.Text = S["HrtfWizStage2Title"];
                stageSub.Text = S["HrtfWizStage2Sub"];
                candidates = Cavernize.Logic.Spatial.PerceptualHrtfWizardCatalog.Stage2BiometricItdClusters;
            } else {
                stageHeader.Text = S["HrtfWizStage3Title"];
                stageSub.Text = S["HrtfWizStage3Sub"];
                candidates = Cavernize.Logic.Spatial.PerceptualHrtfWizardCatalog.Stage3PhaseAndMagLsModes;
            }

            for (int i = 0; i < candidates.Count; i++) {
                var cand = candidates[i];
                bool isSel = currentStage switch {
                    0 => string.Equals(chosenProfileId, cand.BinauralProfileId, StringComparison.OrdinalIgnoreCase),
                    1 => cand.HeadWidthCm.HasValue && MathF.Abs(chosenHeadWidthCm - cand.HeadWidthCm.Value) < 0.35f,
                    _ => cand.MagLsMode.HasValue && chosenMagLsOrder == (int)cand.MagLsMode.Value &&
                         cand.MinimumPhase.HasValue && chosenMinPhase == cand.MinimumPhase.Value
                };

                LinearLayout row = HRow();
                row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                row.Background = OneUiTheme.CreateRoundedDrawable(
                    this,
                    isSel ? OneUiTheme.WithAlpha(ColAccent, 82) : OneUiTheme.SurfaceModalRow,
                    16,
                    isSel ? OneUiTheme.Primary : OneUiTheme.Divider,
                    isSel ? 1.5f : 1f);
                OneUiTheme.AttachRecoil(row, 0.97f);

                LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                TextView titleTv = Label(S[cand.LabelKey], 13f, isSel ? ColText : OneUiTheme.TextPrimary);
                titleTv.Typeface = isSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                titleTv.SetSingleLine(true);
                titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                TextView descTv = Label(S[cand.DescKey], 11.2f, isSel ? OneUiTheme.TextSecondary : OneUiTheme.TextMuted);
                col.AddView(titleTv);
                col.AddView(descTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });
                row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                if (isSel) {
                    row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
                }

                row.Click += (_, _) => {
                    if (currentStage == 0 && !string.IsNullOrEmpty(cand.BinauralProfileId)) {
                        chosenProfileId = cand.BinauralProfileId;
                        chosenClusterId = cand.Id;
                    } else if (currentStage == 1 && cand.HeadWidthCm.HasValue) {
                        chosenHeadWidthCm = cand.HeadWidthCm.Value;
                    } else if (currentStage == 2) {
                        if (cand.MinimumPhase.HasValue) chosenMinPhase = cand.MinimumPhase.Value;
                        if (cand.MagLsMode.HasValue) chosenMagLsOrder = (int)cand.MagLsMode.Value;
                    }
                    ApplyLivePreview();
                    RenderStage();
                };
                optionsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(8)
                });
            }

            Button cueBtn = PillButton(S["HrtfWizPlayCueBtn"], () => {
                lock (draftSync) {
                    if (reverted) {
                        return;
                    }
                    hasPreviewed = true;
                }
                string pid = chosenProfileId;
                float hw = chosenHeadWidthCm;
                Task.Run(() => {
                    lock (draftSync) {
                        if (reverted || uiDestroyed) {
                            return;
                        }
                        BinauralProfileService.ApplyProfile(this, store, pid, out _);
                        BinauralProfileService.SyncToSceneHub(playerEngine?.SceneHub, store, S);
                    }
                    int sr = VirtualizerFilter.FilterSampleRate >= 8000 ? VirtualizerFilter.FilterSampleRate : 48000;
                    float[] cue = Cavernize.Logic.Spatial.PerceptualHrtfWizardCatalog.SynthesizeAuditionCueBinaural(sr, hw);
                    lock (draftSync) {
                        if (reverted || uiDestroyed) {
                            return;
                        }
                    }
                    PlayStereoFloatBufferAsync(cue, sr);
                    lock (draftSync) {
                        if (reverted || uiDestroyed) {
                            StopCalibrationTonePlayback();
                        }
                    }
                });
            });
            optionsHost.AddView(cueBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(2),
                BottomMargin = Dp(10)
            });

            LinearLayout navRow = HRow();
            Button backBtn = PillButton(S["HrtfWizBackBtn"], () => {
                if (currentStage > 0) {
                    currentStage--;
                    RenderStage();
                }
            });
            backBtn.Enabled = currentStage > 0;
            backBtn.Alpha = currentStage > 0 ? 1f : 0.45f;
            Button nextOrFinishBtn = PrimaryButton(
                currentStage < 2 ? S["HrtfWizNextBtn"] : S["HrtfWizApplyBtn"],
                () => {
                    if (currentStage < 2) {
                        currentStage++;
                        RenderStage();
                    } else {
                        committed = true;
                        StopCalibrationTonePlayback();
                        ApplyLivePreview();
                        RefreshPersonalizationUi();
                        activeDlg?.Dismiss();
                        ShowToast(S["HrtfWizAppliedToast"]);
                    }
                });
            navRow.AddView(backBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
            navRow.AddView(nextOrFinishBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.35f));
            optionsHost.AddView(navRow);
        }

        RenderStage();
        activeDlg = OneUiModalDialog.Show(
            this,
            S["HrtfWizTitle"],
            customContent: body,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["CancelBtn"],
            onPositive: () => {
                StopCalibrationTonePlayback();
                RevertDraftIfNeeded();
            },
            onHelp: () => ShowModalGuidedHelp(S["HrtfWizHelpTitle"], S["HrtfWizHelpBody"], OneUiIconType.Sparkles),
            helpContentDesc: S["HrtfWizHelpTitle"]);
        if (activeDlg != null) {
            activeDlg.DismissEvent += (_, _) => {
                StopCalibrationTonePlayback();
                RevertDraftIfNeeded();
            };
        }
    }

    void PlayHptfTestToneAsync(bool sweep) {
        const int sr = 48000;
        float[] stereo = sweep
            ? Cavern.Filters.HptfNotchFilter.GenerateCalibrationSweepStereo(
                Cavern.Filters.HptfNotchFilter.MinFrequencyHz,
                Cavern.Filters.HptfNotchFilter.MaxFrequencyHz,
                sr,
                4.0)
            : Cavern.Filters.HptfNotchFilter.GenerateCalibrationToneStereo(store.HptfNotchFreqHz, sr, 2.2f);
        if (store.HptfNotchEnabled && store.HptfNotchGainDb < -0.05f) {
            Cavern.Filters.HptfNotchFilter liveNotch = new(sr, 2);
            liveNotch.SetParameters(true, store.HptfNotchFreqHz, store.HptfNotchGainDb, store.HptfNotchQ);
            liveNotch.ProcessInterleaved(stereo, 0, stereo.Length / 2, 2);
        }
        ShowToast(sweep
            ? S["HptfSweepPlayingToast"]
            : string.Format(S["HptfTonePlayingToast"], store.HptfNotchFreqHz));
        PlayStereoFloatBufferAsync(stereo, sr);
    }

    void StopCalibrationTonePlayback() {
        CancellationTokenSource prev = Interlocked.Exchange(ref calibrationAudioCts, null);
        if (prev != null) {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }
    }

    void PlayStereoFloatBufferAsync(float[] stereo, int sampleRate) {
        if (stereo == null || stereo.Length < 2) {
            return;
        }
        StopCalibrationTonePlayback();
        CancellationTokenSource cts = new();
        calibrationAudioCts = cts;
        CancellationToken token = cts.Token;

        _ = Task.Run(() => {
            Android.Media.AudioTrack track = null;
            try {
                int minBytes = Android.Media.AudioTrack.GetMinBufferSize(
                    sampleRate,
                    Android.Media.ChannelOut.Stereo,
                    Android.Media.Encoding.PcmFloat);
                if (minBytes <= 0) {
                    return;
                }
                Android.Media.AudioAttributes attrs = new Android.Media.AudioAttributes.Builder()
                    .SetUsage(Android.Media.AudioUsageKind.Media)
                    .SetContentType(Android.Media.AudioContentType.Music)
                    .Build();
                Android.Media.AudioFormat fmt = new Android.Media.AudioFormat.Builder()
                    .SetEncoding(Android.Media.Encoding.PcmFloat)
                    .SetChannelMask(Android.Media.ChannelOut.Stereo)
                    .SetSampleRate(sampleRate)
                    .Build();
                track = new Android.Media.AudioTrack.Builder()
                    .SetAudioAttributes(attrs)
                    .SetAudioFormat(fmt)
                    .SetTransferMode(Android.Media.AudioTrackMode.Stream)
                    .SetBufferSizeInBytes(Math.Max(minBytes, 4096 * 2 * sizeof(float)))
                    .Build();
                if (track.State != Android.Media.AudioTrackState.Initialized) {
                    return;
                }
                track.Play();
                int chunk = 2048 * 2;
                for (int offset = 0; offset < stereo.Length && !token.IsCancellationRequested; offset += chunk) {
                    int count = Math.Min(chunk, stereo.Length - offset);
                    track.Write(stereo, offset, count, Android.Media.WriteMode.Blocking);
                }
            } catch (Exception ex) {
                AppLogger.Warn($"Calibration audio playback interrupted: {ex.Message}");
            } finally {
                if (track != null) {
                    try { track.Stop(); } catch { }
                    try { track.Release(); } catch { }
                    try { track.Dispose(); } catch { }
                }
            }
        });
    }

}
