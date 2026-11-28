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
    void BuildSettingsAudioCards() {
        // 1. Spatial 3D Upmixing (DirAC 1024-pt STFT always active internally)
        LinearLayout upmixCard = Card(S["UpmixTitle"], OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix);
        upmixArbitratedHintText = Label(S["UpmixHiddenAtmosHint"], 11.4f, OneUiTheme.PeachText);
        upmixArbitratedHintText.Visibility = ViewStates.Gone;
        upmixArbitratedHintText.SetPadding(0, 0, 0, Dp(6));
        upmixCard.AddView(upmixArbitratedHintText);

        upmixControlsGroup = new LinearLayout(this) { Orientation = Orientation.Vertical };
        upmixModeSelector = new OneUiInlineSelector(this, S["UpmixModeLabel"]);
        upmixModeSelector.SetItems([S["UpmixModeDirect"], S["UpmixModeSurround"], S["UpmixModeFull3D"]], ResolveUpmixModeIndex());
        upmixModeSelector.SelectionChanged += pos => SetClassicUpmixMode(pos);
        upmixControlsGroup.AddView(WrapControlWithHelp(upmixModeSelector, S["UpmixModeLabel"].TrimEnd(':'), S["TipUpmixMode"], bottomMarginDp: 2));

        atmosAwareUpmixSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        AddDivider(atmosAwareUpmixSection);
        atmosAwareUpmixSwitch = AddSwitchRow(atmosAwareUpmixSection, S["AtmosAwareUpmix"], store.AtmosAwareUpmix, SetAtmosAwareUpmix, helpBody: S["TipAtmosAwareUpmix"]);
        atmosAwareUpmixSection.Visibility = (store.MatrixUpmixing || store.CavernizeUpmix) ? ViewStates.Visible : ViewStates.Gone;
        upmixControlsGroup.AddView(atmosAwareUpmixSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        AddDivider(upmixControlsGroup);
        (upmixEffectBar, _) = AddSliderRow(upmixControlsGroup, S["UpmixEffect"], 0, 100, (int)Math.Round(store.UpmixEffect * 100), v => {
            if (IsCoordinatorUpdating) return;
            store.UpmixEffect = v * 0.01f;
            _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
        }, "%", defaultValue: 75, helpBody: S["TipUpmixEffect"]);
        (upmixSmoothBar, _) = AddSliderRow(upmixControlsGroup, S["UpmixSmooth"], 0, 100, (int)Math.Round(store.UpmixSmoothness * 100), v => {
            if (IsCoordinatorUpdating) return;
            store.UpmixSmoothness = v * 0.01f;
            _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
        }, "%", defaultValue: 80, helpBody: S["TipUpmixSmooth"]);
        Button resetUpmixBtn = PillButton(S["ResetUpmix"], () => {
            store.UpmixEffect = 0.75f;
            store.UpmixSmoothness = 0.80f;
            SetAtmosAwareUpmix(true);
            SetClassicUpmixMode(0);
            SyncSlider(upmixEffectBar, 75, 0, 100, "%");
            SyncSlider(upmixSmoothBar, 80, 0, 100, "%");
        });
        upmixControlsGroup.AddView(resetUpmixBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        upmixCard.AddView(upmixControlsGroup);
        RegisterSettingsCard(upmixCard, categoryIndex: 0, isProOnly: false, "upmix matrix cavernize dirac stft atmos spatial 3d surround direct");

        // 2. Room Acoustics & Pre-Render Gain
        LinearLayout acousticsCard = Card(S["RoomAcousticsTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        AddSliderRow(acousticsCard, S["RenderGain"], -24, 24, (int)Math.Round(store.RenderGainDb), v => {
            store.RenderGainDb = v;
            store.ApplyGlobals();
        }, " dB", defaultValue: 0, helpBody: S["TipRenderGain"]);
        AddSliderRow(acousticsCard, S["HeightSqueeze"], 0, 100, (int)Math.Round(store.HeightSqueezePercent), v => {
            store.HeightSqueezePercent = v;
            store.ApplyGlobals();
        }, "%", defaultValue: 0, helpBody: S["TipHeightSqueeze"]);

        OneUiInlineSelector envSelector = new(this, S["EnvType"]);
        string[] envNames = Enum.GetNames(typeof(Environments));
        envSelector.SetItems(envNames, Math.Clamp(store.EnvironmentTypeIndex, 0, envNames.Length - 1));
        envSelector.SelectionChanged += pos => {
            store.EnvironmentTypeIndex = pos;
            store.ApplyGlobals();
            if (playerEngine != null) {
                playerEngine.EnvironmentType = (Environments)pos;
            }
            UpdateRoomCanvas();
            RefreshSettingsCategorySubtitles();
        };
        acousticsCard.AddView(WrapControlWithHelp(envSelector, S["EnvType"].TrimEnd(':'), S["TipEnvType"]));

        AddDivider(acousticsCard);
        LinearLayout roomSizeHeader = HRow();
        roomSizeHeader.AddView(Label(S["RoomSize"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        roomSizeHeader.AddView(CreateInlineHelpBtn(S["RoomSize"], S["TipRoomSize"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        acousticsCard.AddView(roomSizeHeader);
        LinearLayout roomDimRow = HRow();
        roomDimRow.SetPadding(0, Dp(4), 0, Dp(2));
        EditText rx = NumBox(store.RoomSizeX.ToString("0.0", CultureInfo.InvariantCulture));
        EditText ry = NumBox(store.RoomSizeY.ToString("0.0", CultureInfo.InvariantCulture));
        EditText rz = NumBox(store.RoomSizeZ.ToString("0.0", CultureInfo.InvariantCulture));
        Button applyRoomBtn = PillButton(S["OkBtn"], () => {
            if (float.TryParse(rx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float vx) &&
                float.TryParse(ry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float vy) &&
                float.TryParse(rz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float vz)) {
                store.RoomSizeX = Math.Clamp(vx, 1f, 50f);
                store.RoomSizeY = Math.Clamp(vy, 1f, 50f);
                store.RoomSizeZ = Math.Clamp(vz, 1f, 50f);
                store.ApplyGlobals();
                if (playerEngine != null) {
                    playerEngine.EnvironmentSize = Listener.EnvironmentSize;
                }
                ApplyAndRefreshExternalization();
                SyncRoomSlidersFromEngine();
                UpdateRoomCanvas();
                RefreshSettingsCategorySubtitles();
            }
        }, active: true);
        roomDimRow.AddView(rx, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        roomDimRow.AddView(ry, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        roomDimRow.AddView(rz, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        roomDimRow.AddView(applyRoomBtn, new LinearLayout.LayoutParams(Dp(60), ViewGroup.LayoutParams.WrapContent));
        acousticsCard.AddView(roomDimRow);

        AddDivider(acousticsCard);
        settingsRoomInvertOrbitSwitch = AddSwitchRow(
            acousticsCard,
            S["RoomInvertOrbitTitle"],
            store.RoomInvertOrbitDrag,
            v => {
                if (IsCoordinatorUpdating) return;
                store.RoomInvertOrbitDrag = v;
                SyncRoomInvertOrbitUi();
            },
            helpBody: S["TipRoomInvertOrbit"]);

        AddDivider(acousticsCard);
        lyricsHighlightSelector = new OneUiInlineSelector(this, S["LyricsHighlightStyleTitle"]);
        string[] lyricsStyleLabels = [
            S["LyricsStyleSmoothGlow"],
            S["LyricsStyleMinimalClean"],
            S["LyricsStyleSoftPill"]
        ];
        lyricsHighlightSelector.SetItems(lyricsStyleLabels, Math.Clamp(store.LyricsHighlightStyleIndex, 0, lyricsStyleLabels.Length - 1));
        lyricsHighlightSelector.SelectionChanged += pos => {
            store.LyricsHighlightStyleIndex = pos;
            if (musicLyricsView != null) {
                musicLyricsView.WordHighlightStyle = (LyricsWordHighlightStyle)Math.Clamp(pos, 0, 2);
            }
        };
        acousticsCard.AddView(WrapControlWithHelp(
            lyricsHighlightSelector,
            S["LyricsHighlightStyleTitle"].TrimEnd(':'),
            S["TipLyricsHighlightStyle"],
            bottomMarginDp: 2));

        RegisterSettingsCard(acousticsCard, categoryIndex: 0, isProOnly: false, "room acoustics gain height squeeze dimensions environment orbit drag invert lyrics highlight glow word");

        // 3. Official Binaural Profile (HRIR / BRIR) & QuickEQ Calibration

        // Psychoacoustics & Studio Loudness Standards (ISO 226:2023, ITU-R BS.1770-5 / EBU R128, Spatial Dialog, 4x True-Peak Limiter)
        LinearLayout slot6bCard = Card(S["Slot6BCardTitle"], OneUiIconType.SpeakerHigh, OneUiTheme.IconSliderMedia);

        loudnessMeterText = Label(string.Format(S["LoudnessMeterIdle"], store.ResolveTruePeakCeilingDbtp()), 12f, OneUiTheme.PrimaryHigh);
        slot6bCard.AddView(loudnessMeterText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(4)
        });

        loudnessStatusText = Label("", 11.5f, OneUiTheme.TextSub);
        slot6bCard.AddView(loudnessStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        iso226EnabledSwitch = AddSwitchRow(slot6bCard, S["Iso226EnableTitle"], store.Iso226Enabled, v => {
            store.Iso226Enabled = v;
            ApplyAndRefreshLoudness();
        }, helpBody: S["TipIso226"]);

        iso226DetailsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.Iso226Enabled ? ViewStates.Visible : ViewStates.Gone
        };
        iso226TrackVolSwitch = AddSwitchRow(iso226DetailsSection, S["Iso226TrackVolTitle"], store.Iso226TrackVolume, v => {
            store.Iso226TrackVolume = v;
            ApplyAndRefreshLoudness();
        });

        (iso226RefSplBar, _) = AddSliderRow(
            iso226DetailsSection,
            S["Iso226RefSplTitle"],
            70,
            85,
            Math.Clamp((int)Math.Round(store.Iso226ReferenceSplPhon), 70, 85),
            v => {
                store.Iso226ReferenceSplPhon = v;
                ApplyAndRefreshLoudness();
            },
            " phon",
            defaultValue: 80);

        (iso226ListenSplBar, _) = AddSliderRow(
            iso226DetailsSection,
            S["Iso226ListenSplTitle"],
            40,
            95,
            Math.Clamp((int)Math.Round(store.Iso226ListeningSplPhon), 40, 95),
            v => {
                store.Iso226ListeningSplPhon = v;
                ApplyAndRefreshLoudness();
            },
            " phon",
            defaultValue: 68);
        slot6bCard.AddView(iso226DetailsSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        AddDivider(slot6bCard);

        loudnessNormSelector = new OneUiInlineSelector(this, S["LoudnessNormTitle"]);
        string[] normLabels = [
            S["LoudnessNormOff"],
            S["LoudnessNormEbu"],
            S["LoudnessNormAtmos"],
            S["LoudnessNormStream"],
            S["LoudnessNormDialnorm"]
        ];
        loudnessNormSelector.SetItems(normLabels, Math.Clamp(store.LoudnessNormModeIndex, 0, normLabels.Length - 1));
        loudnessNormSelector.SelectionChanged += pos => {
            store.LoudnessNormModeIndex = pos;
            ApplyAndRefreshLoudness();
        };
        slot6bCard.AddView(WrapControlWithHelp(loudnessNormSelector, S["LoudnessNormTitle"].TrimEnd(':'), S["TipLoudnessNorm"], bottomMarginDp: 8));

        loudnessNormDetailsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.LoudnessNormModeIndex > 0 ? ViewStates.Visible : ViewStates.Gone
        };
        respectDialnormSwitch = AddSwitchRow(loudnessNormDetailsSection, S["RespectDialnormTitle"], store.RespectDolbyDialnorm, v => {
            store.RespectDolbyDialnorm = v;
            ApplyAndRefreshLoudness();
        }, helpBody: S["TipRespectDialnorm"]);
        slot6bCard.AddView(loudnessNormDetailsSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        AddDivider(slot6bCard);

        dialogEnhanceSwitch = AddSwitchRow(slot6bCard, S["DialogEnhanceTitle"], store.DialogEnhancementEnabled, v => {
            store.DialogEnhancementEnabled = v;
            ApplyAndRefreshLoudness();
        }, helpBody: S["TipDialogEnhance"]);

        dialogDetailsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.DialogEnhancementEnabled ? ViewStates.Visible : ViewStates.Gone
        };
        (dialogBoostBar, _) = AddSliderRow(
            dialogDetailsSection,
            S["DialogBoostTitle"],
            0,
            60,
            Math.Clamp((int)Math.Round(store.DialogEnhancementDb * 10f), 0, 60),
            v => {
                store.DialogEnhancementDb = v * 0.1f;
                if (dialogBoostBar != null) {
                    dialogBoostBar.ValueText = $"+{store.DialogEnhancementDb:0.0} dB";
                    dialogBoostBar.Invalidate();
                }
                ApplyAndRefreshLoudness();
            },
            " dB",
            defaultValue: 30);
        if (dialogBoostBar != null) {
            dialogBoostBar.ValueText = $"+{store.DialogEnhancementDb:0.0} dB";
        }
        slot6bCard.AddView(dialogDetailsSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        AddDivider(slot6bCard);

        truePeakCeilingSelector = new OneUiInlineSelector(this, S["TruePeakCeilingTitle"]);
        string[] ceilLabels = [
            S["TruePeakCeil01"],
            S["TruePeakCeil10"],
            S["TruePeakCeil20"]
        ];
        truePeakCeilingSelector.SetItems(ceilLabels, Math.Clamp(store.TruePeakCeilingIndex, 0, ceilLabels.Length - 1));
        truePeakCeilingSelector.SelectionChanged += pos => {
            store.TruePeakCeilingIndex = pos;
            ApplyAndRefreshLoudness();
        };
        slot6bCard.AddView(WrapControlWithHelp(truePeakCeilingSelector, S["TruePeakCeilingTitle"].TrimEnd(':'), S["TipTruePeakCeiling"], bottomMarginDp: 8));

        Button resetLoudnessMeterBtn = PillButton(S["LoudnessResetMeterBtn"], () => {
            playerEngine?.ResetLoudnessMeter();
            RefreshLiveLoudnessMeterUi();
            ShowToast(S["LoudnessMeterResetToast"]);
        });
        slot6bCard.AddView(resetLoudnessMeterBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4)
        });

        RegisterSettingsCard(slot6bCard, categoryIndex: 0, isProOnly: false, "normalizer limiter normalization psychoacoustics loudness iso 226 phon ebu r128 lufs dialnorm dialog boost true peak ceiling");
        RegisterSettingsCard(BuildFilterStudioProCard(), categoryIndex: 0, isProOnly: true, "filter studio parametric eq cytomic svf nyquist decramped phase spectrum export equalizer apo camilladsp minidsp pipewire fir wav");
    }

    int ResolveUpmixModeIndex() {
        if (store == null) {
            return 0;
        }
        if (store.CavernizeUpmix) {
            return 2;
        }
        return store.MatrixUpmixing ? 1 : 0;
    }

    void SyncUpmixModeUi() {
        bool hideChannelUpmix = ShouldHideChannelUpmixSelectors();
        if (upmixControlsGroup != null) {
            upmixControlsGroup.Visibility = hideChannelUpmix ? ViewStates.Gone : ViewStates.Visible;
        }
        if (upmixArbitratedHintText != null) {
            upmixArbitratedHintText.Visibility = hideChannelUpmix ? ViewStates.Visible : ViewStates.Gone;
            if (hideChannelUpmix) {
                upmixArbitratedHintText.Text = IsActiveTrackMultichannelOrAtmos()
                    ? S["UpmixHiddenAtmosHint"]
                    : S["UpmixHiddenAi3DHint"];
            }
        }
        int idx = ResolveUpmixModeIndex();
        if (upmixModeSelector != null && upmixModeSelector.SelectedIndex != idx) {
            upmixModeSelector.SetSelection(idx, notify: false);
        }
        if (atmosAwareUpmixSwitch != null && store != null && atmosAwareUpmixSwitch.Checked != store.AtmosAwareUpmix) {
            uiCoordinator.RunGuarded(() => atmosAwareUpmixSwitch.Checked = store.AtmosAwareUpmix);
        }
        if (atmosAwareUpmixSection != null) {
            atmosAwareUpmixSection.Visibility = idx > 0 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (!IsCoordinatorUpdating) {
            RefreshSpatialChoreoClarifierUi();
        }
    }

    void SyncRoomInvertOrbitUi() {
        if (store == null) {
            return;
        }
        bool inv = store.RoomInvertOrbitDrag;
        if (roomView != null) {
            roomView.InvertOrbitDrag = inv;
        }
        if (settingsRoomInvertOrbitSwitch != null && settingsRoomInvertOrbitSwitch.Checked != inv) {
            uiCoordinator.RunGuarded(() => settingsRoomInvertOrbitSwitch.Checked = inv);
        }
    }

    void RefreshSettingsTabButtons() {
        if (!IsCoordinatorUpdating) {
            RefreshBinauralProfileUi();
            RefreshAutoEqUi();
            RefreshExternalizationUi();
            RefreshPersonalizationUi();
            RefreshLoudnessUi();
            RefreshHeadTrackingUi();
            RefreshProAudioUi();
            RefreshNeuralAudioUi();
            RefreshLgsrUi();
            SyncUpmixModeUi();
            SyncRoomInvertOrbitUi();
        }
        if (filtersBtn != null) {
            filtersBtn.Text = renderingSettings.RoomCorrection != null
                ? $"{S["ClearFilters"]} ({Path.GetFileName(store.RoomCorrectionPath)})"
                : S["LoadFilters"];
        }
        if (muteBedSwitch != null && muteBedSwitch.Checked != store.MuteBed) {
            muteBedSwitch.Checked = store.MuteBed;
        }
        if (muteGroundSwitch != null && muteGroundSwitch.Checked != store.MuteGround) {
            muteGroundSwitch.Checked = store.MuteGround;
        }
        if (lyricsHighlightSelector != null) {
            int lIdx = Math.Clamp(store.LyricsHighlightStyleIndex, 0, 2);
            if (lyricsHighlightSelector.SelectedIndex != lIdx) {
                lyricsHighlightSelector.SetSelection(lIdx, notify: false);
            }
        }
        if (musicLyricsView != null) {
            musicLyricsView.WordHighlightStyle = (LyricsWordHighlightStyle)Math.Clamp(store.LyricsHighlightStyleIndex, 0, 2);
        }
        if (atmosAwareUpmixSwitch != null && atmosAwareUpmixSwitch.Checked != store.AtmosAwareUpmix) {
            atmosAwareUpmixSwitch.Checked = store.AtmosAwareUpmix;
        }
        SyncSlider(upmixEffectBar, (int)Math.Round(store.UpmixEffect * 100f), 0, 100, "%");
        SyncSlider(upmixSmoothBar, (int)Math.Round(store.UpmixSmoothness * 100f), 0, 100, "%");
        if (speakerVirtSwitch != null && speakerVirtSwitch.Checked != store.SpeakerVirtualizer) {
            speakerVirtSwitch.Checked = store.SpeakerVirtualizer;
        }
        if (force24Switch != null && force24Switch.Checked != store.Force24Bit) {
            force24Switch.Checked = store.Force24Bit;
        }
        if (surroundSwapSwitch != null && surroundSwapSwitch.Checked != store.SurroundSwap) {
            surroundSwapSwitch.Checked = store.SurroundSwap;
        }
        if (wavSkipSwitch != null && wavSkipSwitch.Checked != store.WavChannelSkip) {
            wavSkipSwitch.Checked = store.WavChannelSkip;
        }
        if (reportModeSwitch != null && reportModeSwitch.Checked != store.ReportOnlyMode) {
            reportModeSwitch.Checked = store.ReportOnlyMode;
        }
        if (gradingSwitch != null && gradingSwitch.Checked != store.QualityGrading) {
            gradingSwitch.Checked = store.QualityGrading;
        }
        if (pedanticSwitch != null && pedanticSwitch.Checked != store.Pedantic) {
            pedanticSwitch.Checked = store.Pedantic;
        }
        if (unsafeSwitch != null && unsafeSwitch.Checked != store.Unsafe) {
            unsafeSwitch.Checked = store.Unsafe;
        }
        if (keepTempSwitch != null && keepTempSwitch.Checked != store.KeepTempFiles) {
            keepTempSwitch.Checked = store.KeepTempFiles;
        }
        if (autoCheckUpdatesSwitch != null && autoCheckUpdatesSwitch.Checked != store.AutoCheckUpdates) {
            autoCheckUpdatesSwitch.Checked = store.AutoCheckUpdates;
        }
        RefreshLogSummaryLabel();
        RefreshSettingsCategorySubtitles();
    }

    void ApplyAndRefreshLoudness() {
        if (store == null || playerEngine == null) {
            return;
        }
        playerEngine.ApplyLoudnessConfig(store.BuildLoudnessConfig());
        RefreshLoudnessUi();
        RefreshSettingsCategorySubtitles();
    }

    void RefreshLoudnessUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (iso226EnabledSwitch != null && iso226EnabledSwitch.Checked != store.Iso226Enabled) {
            iso226EnabledSwitch.Checked = store.Iso226Enabled;
        }
        if (iso226DetailsSection != null) {
            iso226DetailsSection.Visibility = store.Iso226Enabled ? ViewStates.Visible : ViewStates.Gone;
        }
        if (iso226TrackVolSwitch != null && iso226TrackVolSwitch.Checked != store.Iso226TrackVolume) {
            iso226TrackVolSwitch.Checked = store.Iso226TrackVolume;
        }
        if (iso226RefSplBar != null) {
            int r = Math.Clamp((int)Math.Round(store.Iso226ReferenceSplPhon), 70, 85);
            if (iso226RefSplBar.Progress != r - 70) {
                iso226RefSplBar.Progress = r - 70;
            }
        }
        if (iso226ListenSplBar != null) {
            int l = Math.Clamp((int)Math.Round(store.Iso226ListeningSplPhon), 40, 95);
            if (iso226ListenSplBar.Progress != l - 40) {
                iso226ListenSplBar.Progress = l - 40;
            }
        }
        if (loudnessNormSelector != null) {
            int nIdx = Math.Clamp(store.LoudnessNormModeIndex, 0, 4);
            if (loudnessNormSelector.SelectedIndex != nIdx) {
                loudnessNormSelector.SetSelection(nIdx, notify: false);
            }
        }
        if (loudnessNormDetailsSection != null) {
            loudnessNormDetailsSection.Visibility = store.LoudnessNormModeIndex > 0 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (respectDialnormSwitch != null && respectDialnormSwitch.Checked != store.RespectDolbyDialnorm) {
            respectDialnormSwitch.Checked = store.RespectDolbyDialnorm;
        }
        if (dialogEnhanceSwitch != null && dialogEnhanceSwitch.Checked != store.DialogEnhancementEnabled) {
            dialogEnhanceSwitch.Checked = store.DialogEnhancementEnabled;
        }
        if (dialogDetailsSection != null) {
            dialogDetailsSection.Visibility = store.DialogEnhancementEnabled ? ViewStates.Visible : ViewStates.Gone;
        }
        if (dialogBoostBar != null) {
            int tenths = Math.Clamp((int)Math.Round(store.DialogEnhancementDb * 10f), 0, 60);
            if (dialogBoostBar.Progress != tenths) {
                dialogBoostBar.Progress = tenths;
            }
            dialogBoostBar.ValueText = $"+{store.DialogEnhancementDb:0.0} dB";
            dialogBoostBar.Invalidate();
        }
        if (truePeakCeilingSelector != null) {
            int cIdx = Math.Clamp(store.TruePeakCeilingIndex, 0, 2);
            if (truePeakCeilingSelector.SelectedIndex != cIdx) {
                truePeakCeilingSelector.SetSelection(cIdx, notify: false);
            }
        }
        RefreshLiveLoudnessMeterUi();
    }

    void RefreshLiveLoudnessMeterUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        var diag = playerEngine?.LoudnessDiagnostics ?? Cavernize.Logic.Spatial.LoudnessStandardsDiagnostics.Default;
        if (loudnessMeterText != null) {
            bool hasSignal = !float.IsNaN(diag.MomentaryLufs) || !float.IsNaN(diag.IntegratedLufs);
            if (!hasSignal) {
                loudnessMeterText.Text = string.Format(S["LoudnessMeterIdle"], store.ResolveTruePeakCeilingDbtp());
                loudnessMeterText.SetTextColor(OneUiTheme.TextMuted);
            } else {
                string mStr = float.IsNaN(diag.MomentaryLufs) ? "--" : $"{diag.MomentaryLufs:0.0}";
                string sStr = float.IsNaN(diag.ShortTermLufs) ? "--" : $"{diag.ShortTermLufs:0.0}";
                string iStr = float.IsNaN(diag.IntegratedLufs) ? "--" : $"{diag.IntegratedLufs:0.0}";
                string dnPart = !float.IsNaN(diag.DialnormLufs) ? $" · DN {diag.DialnormLufs:0} LUFS" : "";
                string grPart = diag.LimiterGainReductionDb < -0.05f ? $" · GR {diag.LimiterGainReductionDb:0.0} dB" : "";
                loudnessMeterText.Text = string.Format(
                    S["LoudnessMeterLive"],
                    mStr,
                    sStr,
                    iStr,
                    diag.LoudnessRangeLu,
                    diag.TruePeakDbtp,
                    dnPart,
                    grPart);
                loudnessMeterText.SetTextColor(diag.LimiterGainReductionDb < -1.0f ? OneUiTheme.PeachText : OneUiTheme.PrimaryHigh);
            }
        }
        if (loudnessStatusText != null) {
            string isoPart = diag.Iso226Active
                ? $"{diag.Iso226EffectivePhon:0} phon (+{diag.Iso226MaxBoostDb:0.0} dB)"
                : (store.Iso226Enabled ? S["Iso226UnityTag"] : S["StatusOffTag"]);
            string diagPart = diag.DialogActive
                ? (diag.DialogIsSpatialMode
                    ? string.Format(S["DialogSpatialTag"], store.DialogEnhancementDb)
                    : string.Format(S["DialogMsLr4Tag"], store.DialogEnhancementDb))
                : S["StatusOffTag"];
            loudnessStatusText.Text = string.Format(
                S["LoudnessStatusSummary"],
                isoPart,
                diagPart,
                store.ResolveTruePeakCeilingDbtp());
            bool anyActive = diag.Iso226Active || diag.DialogActive || store.LoudnessNormModeIndex > 0;
            loudnessStatusText.SetTextColor(anyActive ? OneUiTheme.PrimaryHigh : OneUiTheme.TextSub);
        }
    }

}
