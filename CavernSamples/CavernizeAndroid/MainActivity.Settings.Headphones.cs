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
    static readonly AutoEqNeutralTarget[] AutoEqTargetUiOrder = [
        AutoEqNeutralTarget.NativeAutoEqProfile,
        AutoEqNeutralTarget.Bk5128Jm1Neutral,
        AutoEqNeutralTarget.HarmanNeutralNoBass,
        AutoEqNeutralTarget.DiffuseFieldNeutral,
        AutoEqNeutralTarget.Bk5128DiffuseField
    ];

    TextView autoEqTargetDescText;

    static int AutoEqTargetToUiIndex(int targetIndex) {
        AutoEqNeutralTarget target = (AutoEqNeutralTarget)Math.Clamp(
            targetIndex,
            (int)AutoEqNeutralTarget.DiffuseFieldNeutral,
            (int)AutoEqNeutralTarget.NativeAutoEqProfile);
        int idx = Array.IndexOf(AutoEqTargetUiOrder, target);
        return idx >= 0 ? idx : 0;
    }

    static int AutoEqUiIndexToTargetIndex(int uiIndex) =>
        (int)AutoEqTargetUiOrder[Math.Clamp(uiIndex, 0, AutoEqTargetUiOrder.Length - 1)];

    void UpdateAutoEqTargetDescriptionText() {
        if (autoEqTargetDescText == null) {
            return;
        }
        AutoEqNeutralTarget target = (AutoEqNeutralTarget)Math.Clamp(
            store.AutoEqTargetIndex,
            (int)AutoEqNeutralTarget.DiffuseFieldNeutral,
            (int)AutoEqNeutralTarget.NativeAutoEqProfile);
        (string key, Color color) = target switch {
            AutoEqNeutralTarget.NativeAutoEqProfile => ("AutoEqTargetDescNative", OneUiTheme.TextMuted),
            AutoEqNeutralTarget.Bk5128Jm1Neutral => ("AutoEqTargetDescJm1", OneUiTheme.TextMuted),
            AutoEqNeutralTarget.HarmanNeutralNoBass => ("AutoEqTargetDescLightBass", OneUiTheme.TextMuted),
            AutoEqNeutralTarget.DiffuseFieldNeutral => ("AutoEqTargetDescDfNeutral", OneUiTheme.FunctionalAmber),
            AutoEqNeutralTarget.Bk5128DiffuseField => ("AutoEqTargetDescBk5128Df", OneUiTheme.FunctionalAmber),
            _ => ("AutoEqTargetDescNative", OneUiTheme.TextMuted)
        };
        string next = S[key];
        if (!string.Equals(autoEqTargetDescText.Text, next, StringComparison.Ordinal)) {
            autoEqTargetDescText.Text = next;
        }
        autoEqTargetDescText.SetTextColor(color);
    }

    void BuildSettingsHeadphonesCards() {
        LinearLayout rendCard = Card(S["CalibTitle"], OneUiIconType.Headphones, OneUiTheme.IconStemsCalib);

        LinearLayout profileRow = HRow();
        profileRow.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
        profileRow.SetPadding(Dp(12), Dp(11), Dp(12), Dp(11));
        OneUiTheme.AttachRecoil(profileRow, 0.98f);
        profileRow.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Headphones, OneUiTheme.IconSpatial, 32),
            new LinearLayout.LayoutParams(Dp(32), Dp(32)) { RightMargin = Dp(10) });

        LinearLayout profTextCol = new(this) { Orientation = Orientation.Vertical };
        settingsBinauralTitleText = Label("", 13.2f, ColText);
        settingsBinauralTitleText.SetIncludeFontPadding(false);
        settingsBinauralTitleText.Typeface = OneUiTheme.GetAppleFontBold(this);
        settingsBinauralTitleText.SetSingleLine(true);
        settingsBinauralTitleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        settingsBinauralSubText = Label("", 11.2f, OneUiTheme.TextSub);
        settingsBinauralSubText.SetSingleLine(true);
        settingsBinauralSubText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        profTextCol.AddView(settingsBinauralTitleText);
        profTextCol.AddView(settingsBinauralSubText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });
        profileRow.AddView(profTextCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        profileRow.AddView(new OneUiIconView(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
        profileRow.Click += (_, _) => ShowBinauralProfileSelectorDialog();
        rendCard.AddView(WrapControlWithHelp(profileRow, S["BinauralProfileTitle"], S["TipBinauralProfile"], bottomMarginDp: 8));

        LinearLayout hrirActionsRow = HRow();
        Button importHrirBtn = PillButton(S["BinauralProfileImportBtn"], LaunchCustomHrirImporter);
        Button creditsHrirBtn = PillButton(S["BinauralProfileCreditsBtn"], ShowBinauralLicenseCreditsDialog);
        hrirActionsRow.AddView(importHrirBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
        hrirActionsRow.AddView(creditsHrirBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        rendCard.AddView(hrirActionsRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) });

        AddDivider(rendCard);

        // 3B. Slot 6A: AutoEq Neutral Headphone Engine & Auto-Profile per Device
        autoEqEnabledSwitch = AddSwitchRow(rendCard, S["AutoEqEnableTitle"], store.AutoEqEnabled, v => {
            store.AutoEqEnabled = v;
            ApplyAutoEqRespectingRoute();
            audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
            RefreshAutoEqUi();
        }, helpBody: S["TipAutoEq"]);

        LinearLayout autoEqRow = HRow();
        autoEqRow.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
        autoEqRow.SetPadding(Dp(12), Dp(11), Dp(12), Dp(11));
        OneUiTheme.AttachRecoil(autoEqRow, 0.98f);
        autoEqRow.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sliders, OneUiTheme.IconAtmosUpmix, 32),
            new LinearLayout.LayoutParams(Dp(32), Dp(32)) { RightMargin = Dp(10) });

        LinearLayout autoEqCol = new(this) { Orientation = Orientation.Vertical };
        settingsAutoEqTitleText = Label("", 13.2f, ColText);
        settingsAutoEqTitleText.SetIncludeFontPadding(false);
        settingsAutoEqTitleText.Typeface = OneUiTheme.GetAppleFontBold(this);
        settingsAutoEqTitleText.SetSingleLine(true);
        settingsAutoEqTitleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        settingsAutoEqSubText = Label("", 11.2f, OneUiTheme.TextSub);
        settingsAutoEqSubText.SetSingleLine(true);
        settingsAutoEqSubText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        autoEqCol.AddView(settingsAutoEqTitleText);
        autoEqCol.AddView(settingsAutoEqSubText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });
        autoEqRow.AddView(autoEqCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        autoEqRow.AddView(new OneUiIconView(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
        autoEqRow.Click += (_, _) => ShowAutoEqProfileSelectorDialog();
        rendCard.AddView(autoEqRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        autoEqTargetSelector = new OneUiInlineSelector(this, S["AutoEqTargetTitle"]);
        string[] targetNames = [
            S["AutoEqTargetNative"],
            S["AutoEqTargetBk5128Jm1"],
            S["AutoEqTargetHarmanNoBass"],
            S["AutoEqTargetDfNeutral"],
            S["AutoEqTargetBk5128Df"]
        ];
        autoEqTargetSelector.SetItems(targetNames, AutoEqTargetToUiIndex(store.AutoEqTargetIndex));
        autoEqTargetSelector.SelectionChanged += pos => {
            store.AutoEqTargetIndex = AutoEqUiIndexToTargetIndex(pos);
            ApplyAutoEqRespectingRoute();
            audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
            RefreshAutoEqUi();
        };
        rendCard.AddView(WrapControlWithHelp(autoEqTargetSelector, S["AutoEqTargetTitle"], S["TipAutoEqTarget"], bottomMarginDp: 2));

        autoEqTargetDescText = Label("", 11.2f, OneUiTheme.TextMuted);
        autoEqTargetDescText.SetMaxLines(2);
        autoEqTargetDescText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        rendCard.AddView(autoEqTargetDescText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(1),
            BottomMargin = Dp(8)
        });
        UpdateAutoEqTargetDescriptionText();

        autoEqDoubleEqGuardSwitch = AddSwitchRow(rendCard, S["AutoEqDoubleEqGuardTitle"], store.AutoEqDoubleEqGuard, v => {
            store.AutoEqDoubleEqGuard = v;
            ApplyAutoEqRespectingRoute();
            RefreshAutoEqUi();
        }, helpBody: S["TipAutoEqDoubleEqGuard"]);
        AddDivider(rendCard);

        autoEqAutoSwitchDeviceSwitch = AddSwitchRow(rendCard, S["AutoEqAutoDeviceTitle"], store.AutoEqAutoSwitchByDevice, v => {
            store.AutoEqAutoSwitchByDevice = v;
            if (v) {
                audioDeviceProfileManager?.RefreshActiveDevice(applyAutoSwitch: true);
            }
            RefreshAutoEqUi();
        }, helpBody: S["TipAutoEqAutoDevice"]);

        settingsAudioDeviceText = Label("", 11.2f, OneUiTheme.TextMuted);
        settingsAudioDeviceText.SetSingleLine(true);
        settingsAudioDeviceText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        rendCard.AddView(settingsAudioDeviceText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(8)
        });

        LinearLayout autoEqActionsRow = HRow();
        Button importAutoEqBtn = PillButton(S["AutoEqImportBtn"], LaunchCustomAutoEqImporter);
        Button creditsAutoEqBtn = PillButton(S["AutoEqCreditsBtn"], ShowAutoEqLicenseCreditsDialog);
        autoEqActionsRow.AddView(importAutoEqBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
        autoEqActionsRow.AddView(creditsAutoEqBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        rendCard.AddView(autoEqActionsRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) });

        AddDivider(rendCard);

        filtersBtn = PillButton(S["LoadFilters"], () => {
            if (renderingSettings.RoomCorrection != null) {
                renderingSettings.RoomCorrection = null;
                store.RoomCorrectionPath = "";
                RefreshSettingsTabButtons();
            } else {
                OpenFileWithExplorerForExtensions(
                    S["LoadFilters"],
                    [".txt", ".cfg"],
                    TryLoadRoomFilters,
                    ReqLoadFilters);
            }
        });
        rendCard.AddView(WrapControlWithHelp(filtersBtn, S["LoadFilters"], S["TipQuickEQ"], bottomMarginDp: 8));

        speakerVirtSwitch = AddSwitchRow(rendCard, S["SpeakerVirt"], store.SpeakerVirtualizer, v => store.SpeakerVirtualizer = v, helpBody: S["TipSpeakerVirt"]);
        RegisterSettingsCard(rendCard, categoryIndex: 1, isProOnly: false, "headphones binaural hrir brir sofa autoeq quickeq speaker virtualizer calibration");

        // Low-Latency Audio, USB DAC & 3DoF/6DoF Head Tracking
        LinearLayout slot4Card = Card(S["Slot4CardTitle"], OneUiIconType.Bolt, OneUiTheme.IconSpatial);
        lowLatencyAudioSwitch = AddSwitchRow(slot4Card, S["LowLatencyAudioTitle"], store.LowLatencyAudio, v => {
            store.LowLatencyAudio = v;
            if (audioOutput != null) {
                audioOutput.LowLatencyMode = v;
            }
            RefreshHeadTrackingUi();
        }, helpBody: S["TipLowLatencyAudio"]);
        AddDivider(slot4Card);
        usbDacNativeSrSwitch = AddSwitchRow(slot4Card, S["UsbDacNativeSrTitle"], store.UsbDacNativeSampleRate, v => {
            store.UsbDacNativeSampleRate = v;
            if (playerEngine != null) {
                playerEngine.UsbDacNativeSampleRateEnabled = v;
            }
            if (audioOutput != null) {
                audioOutput.UsbDacNativeSampleRate = v;
                audioOutput.UpdateOutputRoute(audioDeviceProfileManager?.CurrentDevice);
            }
            RefreshHeadTrackingUi();
        }, helpBody: S["TipUsbDacNativeSr"]);

        audioOutputDiagText = Label("", 11.2f, OneUiTheme.TextMuted);
        audioOutputDiagText.SetSingleLine(true);
        audioOutputDiagText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        slot4Card.AddView(audioOutputDiagText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(8)
        });

        AddDivider(slot4Card);
        headTrackingEnabledSwitch = AddSwitchRow(slot4Card, S["HeadTrackingEnableTitle"], store.HeadTrackingEnabled, v => {
            store.HeadTrackingEnabled = v;
            var src = (Cavernize.Logic.Spatial.HeadTrackerSourceKind)Math.Clamp(store.HeadTrackerSourceIndex, 0, 3);
            if (v) {
                RequestBluetoothPermissionsIfNeeded(src);
            }
            headTrackingManager?.ApplySettings();
            audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
            RefreshHeadTrackingUi();
        }, helpBody: S["TipHeadTracking"]);

        headTrackerSourceSelector = new OneUiInlineSelector(this, S["HeadTrackerSourceTitle"]);
        string[] trackerLabels = [
            S["HeadTrackerManual"],
            S["HeadTrackerGalaxyBuds"],
            S["HeadTrackerAndroidImu"],
            S["HeadTrackerBleOsc"]
        ];
        headTrackerSourceSelector.SetItems(trackerLabels, Math.Clamp(store.HeadTrackerSourceIndex, 0, trackerLabels.Length - 1));
        headTrackerSourceSelector.SelectionChanged += pos => {
            store.HeadTrackerSourceIndex = pos;
            var src = (Cavernize.Logic.Spatial.HeadTrackerSourceKind)Math.Clamp(pos, 0, 3);
            if (store.HeadTrackingEnabled) {
                RequestBluetoothPermissionsIfNeeded(src);
            }
            headTrackingManager?.ApplySettings();
            RefreshHeadTrackingUi();
        };
        LinearLayout htSrcWrap = WrapControlWithHelp(headTrackerSourceSelector, S["HeadTrackerSourceTitle"].TrimEnd(':'), S["TipHeadTrackerSource"], bottomMarginDp: 8);
        htSrcWrap.SetPadding(0, Dp(4), 0, 0);
        slot4Card.AddView(htSrcWrap);

        headTrackingAutoRecenterSwitch = AddSwitchRow(slot4Card, S["HeadTrackingAutoRecenterTitle"], store.HeadTrackingAutoRecenter, v => {
            store.HeadTrackingAutoRecenter = v;
            headTrackingManager?.ApplySettings();
        });
        AddDivider(slot4Card);
        headTrackingLockRollSwitch = AddSwitchRow(slot4Card, S["HeadTrackingLockRollTitle"], store.HeadTrackingLockRoll, v => {
            store.HeadTrackingLockRoll = v;
            headTrackingManager?.ApplySettings();
        });
        AddDivider(slot4Card);
        (headTrackingPredictBar, _) = AddSliderRow(
            slot4Card,
            S["HeadTrackingPredictMs"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.HeadTrackingPredictionMs), 0, 100),
            v => {
                store.HeadTrackingPredictionMs = v;
                headTrackingManager?.ApplySettings();
            },
            " ms",
            defaultValue: 25,
            helpBody: S["TipHeadTrackingPredict"]);

        headTrackingStatusText = Label("", 11.2f, OneUiTheme.TextSub);
        headTrackingStatusText.SetSingleLine(true);
        headTrackingStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        slot4Card.AddView(headTrackingStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(8)
        });

        LinearLayout htActionsRow = HRow();
        Button recenterNowBtn = PillButton(S["HeadTrackingRecenterBtn"], () => {
            headTrackingManager?.Recenter();
            ShowToast(S["HeadTrackingRecenteredToast"]);
        }, active: true);
        headTrackingOscPortBtn = PillButton(string.Format(S["HeadTrackingOscPortBtn"], store.HeadTrackingOscPort), ShowOscPortModal);
        htActionsRow.AddView(recenterNowBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
        htActionsRow.AddView(headTrackingOscPortBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        slot4Card.AddView(htActionsRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        RegisterSettingsCard(slot4Card, categoryIndex: 3, isProOnly: false, "low latency usb dac head tracking imu galaxy buds ble osc gyro");

    }

    void RefreshBinauralProfileUi() {
        string activeId = BinauralProfileService.GetEffectiveProfileId(store);
        BinauralProfileInfo info = BinauralProfileService.FindById(activeId) ?? BinauralProfileService.FindById(BinauralProfileService.DefaultProfileId);
        string title = BinauralProfileService.GetActiveDisplayTitle(store, S);
        string sub = info != null ? S[info.SubtitleKey] : "";
        if (settingsBinauralTitleText != null) {
            settingsBinauralTitleText.Text = title;
        }
        if (settingsBinauralSubText != null) {
            settingsBinauralSubText.Text = sub;
        }
        if (roomBinauralProfileText != null) {
            roomBinauralProfileText.Text = title;
        }
        refreshBentoTilesUi?.Invoke();
    }

    void LaunchCustomHrirImporter() {
        OpenFileWithExplorerForExtensions(
            S["LoadHRIR"],
            [".sofa", ".wav"],
            ImportCustomBinauralFile,
            ReqLoadHrir);
    }

    void ImportCustomBinauralFile(string path) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        Task.Run(() => {
            playerEngine?.RequestSmoothTransition();
            bool ok = BinauralProfileService.ImportCustomFile(this, store, path, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (ok) {
                    if (playerEngine != null) {
                        playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
                        BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                    }
                    AutoEqService.SyncBinauralModeToEngine(store, playerEngine);
                    ApplyAndRefreshExternalization();
                    audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
                    RefreshBinauralProfileUi();
                    RefreshPersonalizationUi();
                    RefreshAutoEqUi();
                    RefreshMusicPlayerControlsUi();
                    RefreshSettingsCategorySubtitles();
                    ShowToast(string.Format(S["BinauralProfileApplied"], BinauralProfileService.GetActiveDisplayTitle(store, S)));
                } else {
                    ShowError(err ?? S["HrirLoadFailedFallback"]);
                }
            });
        });
    }

    void ApplyBinauralProfileAsync(string profileId, bool showToast = true) {
        Task.Run(() => {
            playerEngine?.RequestSmoothTransition();
            bool ok = BinauralProfileService.ApplyProfile(this, store, profileId, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (ok) {
                    if (playerEngine != null) {
                        playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
                        BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                    }
                    AutoEqService.SyncBinauralModeToEngine(store, playerEngine);
                    ApplyAndRefreshExternalization();
                    audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
                    RefreshBinauralProfileUi();
                    RefreshPersonalizationUi();
                    RefreshAutoEqUi();
                    RefreshMusicPlayerControlsUi();
                    RefreshSettingsCategorySubtitles();
                    if (showToast) {
                        ShowToast(string.Format(S["BinauralProfileApplied"], BinauralProfileService.GetActiveDisplayTitle(store, S)));
                    }
                } else {
                    ShowError(err ?? S["BinauralApplyFailedFallback"]);
                }
            });
        });
    }

    void CycleThKolnDistance() {
        if (!BinauralProfileService.HasVariableDistance(store)) {
            return;
        }
        string nextId = BinauralProfileService.GetNextDistanceProfileId(store);
        ApplyBinauralProfileAsync(nextId, showToast: false);
    }

    void ApplyMinimumPhaseAsync(bool enabled) {
        Task.Run(() => {
            playerEngine?.RequestSmoothTransition();
            bool ok = BinauralProfileService.SetMinimumPhase(this, store, enabled, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (ok) {
                    if (playerEngine != null) {
                        BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                    }
                    RefreshBinauralProfileUi();
                    RefreshPersonalizationUi();
                    RefreshMusicPlayerControlsUi();
                    RefreshSettingsCategorySubtitles();
                } else {
                    ShowError(err ?? S["MinPhaseUpdateFailedFallback"]);
                }
            });
        });
    }

    void ShowBinauralLicenseCreditsDialog() {
        string credits = BinauralProfileService.ReadLicenseCredits(this);
        ShowMessageDialog(S["BinauralProfileCreditsBtn"], credits, OneUiIconType.Info, monospace: false);
    }


    void ShowBinauralProfileSelectorDialog() {
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Dialog activeDlg = null;
        string currentId = BinauralProfileService.GetEffectiveProfileId(store);

        // Universal Mathematical Minimum-Phase + ITD toggle card (applies to all anechoic HRIRs, auto-bypassed on BRIRs)
        LinearLayout minPhaseCard = HRow();
        minPhaseCard.SetPadding(Dp(12), Dp(10), Dp(10), Dp(10));
        minPhaseCard.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            store.MinimumPhaseHrir ? OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 48) : OneUiTheme.SurfaceModalRow,
            16f,
            store.MinimumPhaseHrir ? OneUiTheme.IconSpatial : OneUiTheme.BorderSubtle,
            1f);
        LinearLayout mpTextCol = new(this) { Orientation = Orientation.Vertical };
        TextView mpTitle = Label(S["BinauralMinPhaseTitle"], 12.8f, ColText);
        mpTitle.SetIncludeFontPadding(false);
        mpTitle.Typeface = OneUiTheme.GetAppleFontBold(this);
        TextView mpSub = Label(S["BinauralMinPhaseSub"], 10.8f, OneUiTheme.TextSecondary);
        mpTextCol.AddView(mpTitle);
        mpTextCol.AddView(mpSub, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });
        OneUiSwitch mpSwitch = new(this) { Checked = store.MinimumPhaseHrir };
        mpSwitch.CheckedChange += (_, e) => {
            bool on = e.IsChecked;
            minPhaseCard.Background = OneUiTheme.CreateRoundedDrawable(
                this,
                on ? OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 48) : OneUiTheme.SurfaceModalRow,
                16f,
                on ? OneUiTheme.IconSpatial : OneUiTheme.BorderSubtle,
                1f);
            ApplyMinimumPhaseAsync(on);
        };
        mpTextCol.Click += (_, _) => mpSwitch.Checked = !mpSwitch.Checked;
        minPhaseCard.AddView(mpTextCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        minPhaseCard.AddView(mpSwitch, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        list.AddView(minPhaseCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        (BinauralProfileGroup group, string headerKey)[] groupOrder = [
            (BinauralProfileGroup.SadieD1, "BinauralGroupSadieD1"),
            (BinauralProfileGroup.ThKoln, "BinauralGroupThKoln"),
            (BinauralProfileGroup.SadieD2, "BinauralGroupSadieD2"),
            (BinauralProfileGroup.Standard, "BinauralGroupStandard"),
            (BinauralProfileGroup.Custom, "BinauralGroupCustom")
        ];

        foreach (var (group, headerKey) in groupOrder) {
            List<BinauralProfileInfo> items = [];
            for (int i = 0; i < BinauralProfileService.Profiles.Count; i++) {
                BinauralProfileInfo p = BinauralProfileService.Profiles[i];
                if (p.Group != group) continue;
                if (p.IsCustom && (string.IsNullOrEmpty(store.HrirPath) || !File.Exists(store.HrirPath))) continue;
                if (p.IsThKolnDistanceSeries) continue; // Rendered as one unified multi-distance card below
                items.Add(p);
            }
            bool hasThkSeries = group == BinauralProfileGroup.ThKoln;
            if (items.Count == 0 && !hasThkSeries) continue;

            TextView grpHdr = Label(S[headerKey], 11.5f, OneUiTheme.PeachText);
            grpHdr.Typeface = OneUiTheme.GetAppleFontBold(this);
            grpHdr.SetPadding(Dp(4), Dp(6), Dp(4), Dp(6));
            list.AddView(grpHdr);

            // Unified TH Köln KU100 DFC card with inline laboratory distance selector pills
            if (hasThkSeries) {
                BinauralProfileInfo activeCur = BinauralProfileService.FindById(currentId);
                bool anyThkSelected = activeCur != null && activeCur.IsThKolnDistanceSeries;

                LinearLayout seriesCard = new(this) { Orientation = Orientation.Vertical };
                seriesCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                seriesCard.Background = anyThkSelected
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 72), 16f, OneUiTheme.PrimaryHigh, 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);

                LinearLayout sHead = HRow();
                LinearLayout sCol = new(this) { Orientation = Orientation.Vertical };
                TextView sTitle = Label(S["ProfThkDfcSeriesTitle"], 13f, anyThkSelected ? ColText : OneUiTheme.TextPrimary);
                sTitle.SetIncludeFontPadding(false);
                sTitle.Typeface = anyThkSelected ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                sTitle.SetSingleLine(true);
                sTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                TextView sSub = Label(S["ProfThkDfcSeriesSub"], 11f, anyThkSelected ? OneUiTheme.TextSecondary : OneUiTheme.TextMuted);
                sSub.SetSingleLine(true);
                sSub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                sCol.AddView(sTitle);
                sCol.AddView(sSub, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });
                sHead.AddView(sCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                if (anyThkSelected) {
                    sHead.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
                }
                seriesCard.AddView(sHead);

                LinearLayout distPillRow = HRow();
                distPillRow.SetPadding(0, Dp(8), 0, 0);
                for (int d = 0; d < BinauralProfileService.ThKolnDistanceSeries.Length; d++) {
                    var (distId, _, shortLbl) = BinauralProfileService.ThKolnDistanceSeries[d];
                    bool dSel = string.Equals(currentId, distId, StringComparison.OrdinalIgnoreCase);
                    TextView dpBtn = Label(shortLbl, 10.8f, dSel ? OneUiTheme.OnPrimary : OneUiTheme.TextSecondary);
                    dpBtn.SetIncludeFontPadding(false);
                    dpBtn.Typeface = dSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                    dpBtn.Gravity = GravityFlags.Center;
                    dpBtn.SetSingleLine(true);
                    dpBtn.SetPadding(Dp(4), Dp(6), Dp(4), Dp(6));
                    dpBtn.Background = OneUiTheme.CreateRoundedDrawable(
                        this,
                        dSel ? OneUiTheme.Primary : Color.Argb(34, 255, 255, 255),
                        10f,
                        dSel ? OneUiTheme.PrimaryHigh : OneUiTheme.BorderSubtle,
                        0.8f);
                    OneUiTheme.AttachRecoil(dpBtn, 0.94f);
                    string chosenDistId = distId;
                    dpBtn.Click += (_, _) => {
                        activeDlg?.Dismiss();
                        ApplyBinauralProfileAsync(chosenDistId);
                    };
                    distPillRow.AddView(dpBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                        RightMargin = d < BinauralProfileService.ThKolnDistanceSeries.Length - 1 ? Dp(5) : 0
                    });
                }
                seriesCard.AddView(distPillRow);
                list.AddView(seriesCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }

            foreach (BinauralProfileInfo prof in items) {
                bool isSel = string.Equals(currentId, prof.Id, StringComparison.OrdinalIgnoreCase);
                LinearLayout row = HRow();
                row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 16f, OneUiTheme.PrimaryHigh, 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
                OneUiTheme.AttachRecoil(row, 0.98f);

                LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                string rowTitle = prof.IsCustom && !string.IsNullOrEmpty(store.HrirPath)
                    ? $"{S[prof.TitleKey]} ({Path.GetFileName(store.HrirPath)})"
                    : S[prof.TitleKey];
                TextView titleTv = Label(rowTitle, 13f, isSel ? ColText : OneUiTheme.TextPrimary);
                titleTv.SetIncludeFontPadding(false);
                titleTv.Typeface = isSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                titleTv.SetSingleLine(true);
                titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

                TextView subTv = Label(S[prof.SubtitleKey], 11f, isSel ? OneUiTheme.TextSecondary : OneUiTheme.TextMuted);
                subTv.SetSingleLine(true);
                subTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                col.AddView(titleTv);
                col.AddView(subTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });

                row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                if (isSel) {
                    row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
                }

                string chosenId = prof.Id;
                row.Click += (_, _) => {
                    activeDlg?.Dismiss();
                    ApplyBinauralProfileAsync(chosenId);
                };
                list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
        }

        LinearLayout footerRow = HRow();
        footerRow.SetPadding(0, Dp(6), 0, 0);
        Button importBtn = PillButton(S["BinauralProfileImportBtn"], () => {
            activeDlg?.Dismiss();
            LaunchCustomHrirImporter();
        });
        Button creditsBtn = PillButton(S["BinauralProfileCreditsBtn"], () => {
            activeDlg?.Dismiss();
            ShowBinauralLicenseCreditsDialog();
        });
        footerRow.AddView(importBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
        footerRow.AddView(creditsBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        list.AddView(footerRow);

        activeDlg = OneUiModalDialog.Show(
            this,
            S["BinauralProfileTitle"],
            customContent: list,
            icon: OneUiIconType.Headphones,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["CloseBtn"]);
    }

    void OnAudioDeviceRouteChanged(AudioDeviceRouteInfo device, Cavernize.Logic.Spatial.AudioDeviceProfileBinding binding, bool autoMatched) {
        RunOnUiThread(() => {
            if (uiDestroyed) {
                return;
            }
            headTrackingManager?.OnAudioDeviceChanged(device);
            RefreshBinauralProfileUi();
            RefreshAutoEqUi();
            RefreshHeadTrackingUi();
            ApplyAndRefreshExternalization();
            RefreshPersonalizationUi();
            RefreshMusicPlayerControlsUi();
            if (autoMatched && binding != null && !string.IsNullOrEmpty(binding.AutoEqProfileName)) {
                ShowToast(string.Format(S["AutoEqAutoMatchedToast"], device.DisplayName, binding.AutoEqProfileName));
            }
        });
    }

    void RefreshHeadTrackingUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (lowLatencyAudioSwitch != null && lowLatencyAudioSwitch.Checked != store.LowLatencyAudio) {
            lowLatencyAudioSwitch.Checked = store.LowLatencyAudio;
        }
        if (usbDacNativeSrSwitch != null && usbDacNativeSrSwitch.Checked != store.UsbDacNativeSampleRate) {
            usbDacNativeSrSwitch.Checked = store.UsbDacNativeSampleRate;
        }
        if (headTrackingEnabledSwitch != null && headTrackingEnabledSwitch.Checked != store.HeadTrackingEnabled) {
            headTrackingEnabledSwitch.Checked = store.HeadTrackingEnabled;
        }
        if (headTrackingAutoRecenterSwitch != null && headTrackingAutoRecenterSwitch.Checked != store.HeadTrackingAutoRecenter) {
            headTrackingAutoRecenterSwitch.Checked = store.HeadTrackingAutoRecenter;
        }
        if (headTrackingLockRollSwitch != null && headTrackingLockRollSwitch.Checked != store.HeadTrackingLockRoll) {
            headTrackingLockRollSwitch.Checked = store.HeadTrackingLockRoll;
        }
        if (headTrackerSourceSelector != null) {
            int idx = Math.Clamp(store.HeadTrackerSourceIndex, 0, 3);
            if (headTrackerSourceSelector.SelectedIndex != idx) {
                headTrackerSourceSelector.SetSelection(idx, notify: false);
            }
        }
        if (headTrackingOscPortBtn != null) {
            string nextPortTxt = string.Format(S["HeadTrackingOscPortBtn"], store.HeadTrackingOscPort);
            if (!string.Equals(headTrackingOscPortBtn.Text, nextPortTxt, StringComparison.Ordinal)) {
                headTrackingOscPortBtn.Text = nextPortTxt;
            }
        }
        SyncSlider(headTrackingPredictBar, (int)Math.Round(store.HeadTrackingPredictionMs), 0, 100, " ms");
        if (audioOutputDiagText != null) {
            var diag = audioOutput?.Diagnostics ?? Cavernize.Logic.Spatial.AudioOutputDiagnostics.Default;
            string fmt = diag.IsFloat32 ? "Float32" : "PCM16";
            string latTag = diag.IsLowLatency ? S["AudioOutputLowLatTag"] : S["AudioOutputBalancedTag"];
            string usbTag = diag.IsUsbDacPreferred ? S["AudioOutputUsbTag"] : string.Empty;
            string nextDiagTxt = string.Format(
                S["AudioOutputDiagFormat"],
                diag.SampleRate / 1000.0,
                fmt,
                latTag,
                diag.EstimatedLatencyMs,
                usbTag);
            if (!string.Equals(audioOutputDiagText.Text, nextDiagTxt, StringComparison.Ordinal)) {
                audioOutputDiagText.Text = nextDiagTxt;
            }
        }
        if (headTrackingStatusText != null) {
            string summary = headTrackingManager?.StatusSummary ?? string.Empty;
            string nextHtTxt = (!store.HeadTrackingEnabled || string.IsNullOrWhiteSpace(summary))
                ? S["HeadTrackingStatusIdle"]
                : (headTrackingManager.IsLive ? string.Format(S["HeadTrackingStatusLive"], summary) : summary);
            if (!string.Equals(headTrackingStatusText.Text, nextHtTxt, StringComparison.Ordinal)) {
                headTrackingStatusText.Text = nextHtTxt;
            }
        }
    }

    void ShowOscPortModal() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        TextView hint = Label(S["HeadTrackingOscPortHint"], 12.2f, OneUiTheme.TextSub);
        hint.SetPadding(0, 0, 0, Dp(8));
        content.AddView(hint);

        EditText portBox = NumBox(store.HeadTrackingOscPort.ToString(CultureInfo.InvariantCulture));
        content.AddView(portBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        OneUiModalDialog.Show(
            this,
            S["HeadTrackingOscPortModalTitle"],
            customContent: content,
            icon: OneUiIconType.Bolt,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["OkBtn"],
            onPositive: () => {
                if (int.TryParse(portBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) {
                    store.HeadTrackingOscPort = Math.Clamp(port, 1024, 65535);
                    headTrackingManager?.ApplySettings();
                    RefreshHeadTrackingUi();
                }
            },
            negativeText: S["CancelBtn"]);
    }

    void RefreshAutoEqUi() {
        if (autoEqEnabledSwitch != null && autoEqEnabledSwitch.Checked != store.AutoEqEnabled) {
            autoEqEnabledSwitch.Checked = store.AutoEqEnabled;
        }
        if (autoEqDoubleEqGuardSwitch != null && autoEqDoubleEqGuardSwitch.Checked != store.AutoEqDoubleEqGuard) {
            autoEqDoubleEqGuardSwitch.Checked = store.AutoEqDoubleEqGuard;
        }
        if (autoEqAutoSwitchDeviceSwitch != null && autoEqAutoSwitchDeviceSwitch.Checked != store.AutoEqAutoSwitchByDevice) {
            autoEqAutoSwitchDeviceSwitch.Checked = store.AutoEqAutoSwitchByDevice;
        }
        if (autoEqTargetSelector != null) {
            int idx = AutoEqTargetToUiIndex(store.AutoEqTargetIndex);
            if (autoEqTargetSelector.SelectedIndex != idx) {
                autoEqTargetSelector.SetSelection(idx, notify: false);
            }
        }
        UpdateAutoEqTargetDescriptionText();

        if (settingsAutoEqTitleText != null) {
            bool hasProf = !string.IsNullOrWhiteSpace(store.AutoEqProfileName);
            settingsAutoEqTitleText.Text = hasProf ? store.AutoEqProfileName : S["AutoEqNoneSelected"];
        }
        AudioDeviceRouteInfo dev = audioDeviceProfileManager?.CurrentDevice;
        if (settingsAutoEqSubText != null) {
            var stage = playerEngine?.AutoEqStage;
            if (stage != null && stage.IsEnabled) {
                string guardTag = stage.DoubleEqGuardActive ? $" • {S["AutoEqGuardEngagedTag"]}" : string.Empty;
                settingsAutoEqSubText.Text = string.Format(
                    S["AutoEqActiveSummary"],
                    stage.ActiveSectionCount,
                    stage.EffectivePreampDb,
                    guardTag);
            } else if (store.AutoEqEnabled && dev != null && !dev.IsHeadphoneRoute) {
                settingsAutoEqSubText.Text = string.Format(S["AutoEqBypassedSpeakerSub"], dev.DisplayName);
            } else {
                int total = AutoEqService.GetTotalCatalogCount(this);
                settingsAutoEqSubText.Text = string.Format(S["AutoEqCatalogCountSub"], total);
            }
        }
        if (settingsAudioDeviceText != null) {
            string devLabel = dev != null ? dev.DisplayName : S["AutoEqUnknownDevice"];
            settingsAudioDeviceText.Text = string.Format(S["AutoEqDetectedDevice"], devLabel);
        }
    }

    void ApplyAutoEqRespectingRoute() {
        AutoEqService.ApplySavedToEngine(this, store, playerEngine);
        if (audioDeviceProfileManager?.CurrentDevice is { IsHeadphoneRoute: false } speaker &&
            store.FindDeviceProfile(speaker.DeviceKey) == null) {
            audioDeviceProfileManager.RefreshActiveDevice(applyAutoSwitch: false);
        }
    }

    void LaunchCustomAutoEqImporter() {
        OpenFileWithExplorerForExtensions(
            S["AutoEqImportBtn"],
            [".txt", ".cfg"],
            ImportCustomAutoEqFile,
            ReqLoadAutoEq);
    }

    void ImportCustomAutoEqFile(string path) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        Task.Run(() => {
            bool ok = AutoEqService.ImportCustomEqFile(this, store, path, out var profile, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) {
                    return;
                }
                if (ok && profile != null) {
                    AutoEqService.ApplyProfileToEngine(store, playerEngine, profile);
                    audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
                    RefreshAutoEqUi();
                    ShowToast(string.Format(S["AutoEqAppliedToast"], profile.Name));
                } else {
                    ShowError(err ?? S["AutoEqImportFailedFallback"]);
                }
            });
        });
    }

    void ShowAutoEqLicenseCreditsDialog() {
        string credits = AutoEqService.ReadLicenseCredits(this);
        OneUiModalDialog.Show(
            this,
            S["AutoEqCreditsBtn"],
            message: credits,
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["CloseBtn"]);
    }

    void ShowAutoEqProfileSelectorDialog() {
        Dialog activeDlg = null;
        bool autoEqDlgClosed = false;
        Handler searchHandler = uiHandler ?? new Handler(Looper.MainLooper);
        Java.Lang.Runnable searchDebounceRunnable = null;
        LinearLayout root = new(this) { Orientation = Orientation.Vertical };

        LinearLayout searchPill = HRow();
        searchPill.BaselineAligned = false;
        searchPill.SetGravity(GravityFlags.CenterVertical);
        searchPill.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
        searchPill.SetPadding(Dp(12), Dp(4), Dp(8), Dp(4));

        OneUiIconView searchIco = new(this, OneUiIconType.Search, OneUiTheme.TextMuted);
        searchPill.AddView(searchIco, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { RightMargin = Dp(8) });

        EditText searchBox = new(this) {
            Hint = S["AutoEqSearchHint"]
        };
        searchBox.SetSingleLine(true);
        searchBox.SetTextColor(ColText);
        searchBox.SetHintTextColor(OneUiTheme.TextMuted);
        searchBox.SetTextSize(Android.Util.ComplexUnitType.Sp, 13.5f);
        searchBox.SetBackgroundColor(Color.Transparent);
        searchBox.SetPadding(0, Dp(6), 0, Dp(6));
        searchPill.AddView(searchBox, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });

        FrameLayout clearSearchBtn = new(this) {
            Visibility = ViewStates.Gone,
            ContentDescription = S["LibClearSearchDesc"]
        };
        clearSearchBtn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(36, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(clearSearchBtn, 0.88f);
        clearSearchBtn.AddView(new SearchClearIconView(this, OneUiTheme.TextMuted),
            new FrameLayout.LayoutParams(Dp(12), Dp(12)) { Gravity = GravityFlags.Center });
        searchPill.AddView(clearSearchBtn, new LinearLayout.LayoutParams(Dp(24), Dp(24)));

        root.AddView(searchPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout resultsList = new(this) { Orientation = Orientation.Vertical };
        root.AddView(resultsList, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        void PopulateResults(string query) {
            resultsList.RemoveAllViews();
            List<Cavernize.Logic.Spatial.AutoEqCatalogEntry> matches = AutoEqService.SearchCatalog(this, query, maxResults: 40);
            if (matches.Count == 0) {
                TextView emptyTv = Label(S["AutoEqNoResults"], 12.2f, OneUiTheme.TextMuted);
                emptyTv.SetPadding(Dp(4), Dp(10), Dp(4), Dp(10));
                resultsList.AddView(emptyTv);
                return;
            }

            string curId = store.AutoEqProfileId;
            for (int i = 0; i < matches.Count; i++) {
                Cavernize.Logic.Spatial.AutoEqCatalogEntry entry = matches[i];
                bool isSel = store.AutoEqEnabled && string.Equals(curId, entry.Id, StringComparison.OrdinalIgnoreCase);

                LinearLayout row = HRow();
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 16f, OneUiTheme.PrimaryHigh, 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
                row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                OneUiTheme.AttachRecoil(row, 0.985f);

                LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                TextView titleTv = Label(entry.Name, 13f, isSel ? ColText : OneUiTheme.TextPrimary);
                titleTv.SetIncludeFontPadding(false);
                titleTv.Typeface = isSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                titleTv.SetSingleLine(true);
                titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

                string offlineTag = entry.IsBundledOffline ? S["AutoEqOfflineTag"] : S["AutoEqOnlineTag"];
                TextView subTv = Label($"{entry.MeasurementRig} • {offlineTag}", 11f, isSel ? OneUiTheme.TextSecondary : OneUiTheme.TextMuted);
                subTv.SetSingleLine(true);
                subTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

                col.AddView(titleTv);
                col.AddView(subTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });
                row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                if (isSel) {
                    row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
                }

                Cavernize.Logic.Spatial.AutoEqCatalogEntry chosen = entry;
                row.Click += (_, _) => {
                    activeDlg?.Dismiss();
                    ApplyAutoEqCatalogEntryAsync(chosen);
                };
                resultsList.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
        }

        PopulateResults(string.Empty);
        clearSearchBtn.Click += (_, _) => {
            if (searchDebounceRunnable != null) {
                searchHandler.RemoveCallbacks(searchDebounceRunnable);
                searchDebounceRunnable = null;
            }
            searchBox.Text = string.Empty;
            clearSearchBtn.Visibility = ViewStates.Gone;
            PopulateResults(string.Empty);
        };
        searchBox.TextChanged += (_, _) => {
            string query = searchBox.Text ?? string.Empty;
            clearSearchBtn.Visibility = string.IsNullOrEmpty(query) ? ViewStates.Gone : ViewStates.Visible;
            if (searchDebounceRunnable != null) {
                searchHandler.RemoveCallbacks(searchDebounceRunnable);
            }
            searchDebounceRunnable = new Java.Lang.Runnable(() => {
                if (!uiDestroyed && !autoEqDlgClosed) {
                    PopulateResults(query);
                }
            });
            searchHandler.PostDelayed(searchDebounceRunnable, 180);
        };

        LinearLayout footerRow = HRow();
        footerRow.SetPadding(0, Dp(6), 0, 0);
        Button clearBtn = PillButton(S["AutoEqClearBtn"], () => {
            activeDlg?.Dismiss();
            store.AutoEqEnabled = false;
            store.AutoEqProfileId = string.Empty;
            store.AutoEqProfileName = string.Empty;
            AutoEqService.ApplyProfileToEngine(store, playerEngine, null);
            audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
            RefreshAutoEqUi();
        });
        Button importBtn = PillButton(S["AutoEqImportBtn"], () => {
            activeDlg?.Dismiss();
            LaunchCustomAutoEqImporter();
        });
        footerRow.AddView(clearBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        footerRow.AddView(importBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f));
        root.AddView(footerRow);

        activeDlg = OneUiModalDialog.Show(
            this,
            S["AutoEqModalTitle"],
            customContent: root,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["CloseBtn"],
            onHelp: () => ShowModalGuidedHelp(S["AutoEqHelpTitle"], S["AutoEqHelpBody"], OneUiIconType.Sliders),
            helpContentDesc: S["AutoEqHelpTitle"]);
        if (activeDlg != null) {
            activeDlg.DismissEvent += (_, _) => {
                autoEqDlgClosed = true;
                if (searchDebounceRunnable != null) {
                    searchHandler.RemoveCallbacks(searchDebounceRunnable);
                    searchDebounceRunnable = null;
                }
            };
        }
    }

    void ApplyAutoEqCatalogEntryAsync(Cavernize.Logic.Spatial.AutoEqCatalogEntry entry) {
        if (entry == null) {
            return;
        }
        if (!entry.IsBundledOffline) {
            ShowToast(string.Format(S["AutoEqDownloadingToast"], entry.Name));
        }
        Task.Run(async () => {
            try {
                var profile = await AutoEqService.ResolveProfileAsync(this, store, entry.Id).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed) {
                        return;
                    }
                    if (profile != null && profile.Bands.Length > 0) {
                        store.AutoEqProfileId = profile.Id;
                        store.AutoEqProfileName = profile.Name;
                        store.AutoEqEnabled = true;
                        AutoEqService.ApplyProfileToEngine(store, playerEngine, profile);
                        audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
                        RefreshAutoEqUi();
                        ShowToast(string.Format(S["AutoEqAppliedToast"], profile.Name));
                    } else {
                        ShowError(S["AutoEqDownloadFail"]);
                    }
                });
            } catch (Exception ex) {
                AppLogger.Error($"ApplyAutoEqCatalogEntryAsync({entry.Id})", ex);
                RunOnUiThread(() => {
                    if (!uiDestroyed) {
                        ShowError($"{S["AutoEqDownloadFail"]}\n{ex.Message}");
                    }
                });
            }
        });
    }

    void TryLoadRoomFilters(string path) {
        try {
            this.LoadRoomCorrection(path);
            store.RoomCorrectionPath = path;
            RefreshSettingsTabButtons();
            AppLogger.Info($"Loaded room filters: '{path}'");
        } catch (Exception ex) {
            AppLogger.Error("TryLoadRoomFilters", ex);
            renderingSettings.RoomCorrection = null;
            ShowError(ex.Message);
        }
    }

}
