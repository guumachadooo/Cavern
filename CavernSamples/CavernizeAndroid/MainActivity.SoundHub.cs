using System.Collections.Generic;
using System.Linq;

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

public partial class MainActivity {
    LinearLayout diagnosticsDeveloperCardRef;
    LinearLayout soundHubHomeContainer;
    readonly TextView[] soundHubSubtitles = new TextView[4];
    Button soundHubQuickSpatialBtn, soundHubQuickAiBtn;
    View settingsGearOtaDot;
    bool isDeveloperSubPageEnabled;

    /// <summary>
    /// Builds the unified Sound Hub (Tab 1, W55.4.2): hosts 4 Essential audio cards
    /// (Preset &amp; Output, 3D &amp; Space, AI Stems &amp; Vocals, Headphones &amp; Hardware)
    /// with Essential-by-default mode, search, and Collapse-When-Disabled drill-in.
    /// </summary>
    LinearLayout BuildSoundHubTab() {
        isDeveloperSubPageEnabled = GetSharedPreferences("cavernize_settings", FileCreationMode.Private)?
            .GetBoolean("developer_mode_enabled", false) ?? false;

        LinearLayout settingsPage = BuildSettingsTab();
        ApplyTabPageCutoutPadding(settingsPage);

        if (settingsHubTilesContainer != null) {
            settingsHubTilesContainer.RemoveAllViews();
            soundHubHomeContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };

            // Card 1: Preset & Output (Category 0)
            LinearLayout card0 = BuildSoundHubEssentialCard(
                0,
                S["SoundHubPresetOutputTitle"],
                OneUiIconType.Sliders,
                OneUiTheme.IconSpatial,
                out soundHubSubtitles[0]);
            LinearLayout quickOutRow = HRow();
            quickOutRow.SetPadding(0, Dp(8), 0, 0);
            soundHubQuickSpatialBtn = PillButton(S["SoundHubSpatialQuickBtn"], () => {
                ToggleActiveOutputSpatialization();
                RefreshSoundHubCardsUi();
            });
            Button openFsBtn = PillButton(S["FilterStudioTitle"], ShowFilterStudioDialog);
            Button openOutBtn = PillButton(S["SoundHubConfigureBtn"], () => OpenSettingsCategory(0));
            quickOutRow.AddView(soundHubQuickSpatialBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(6) });
            quickOutRow.AddView(openFsBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
            quickOutRow.AddView(openOutBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.9f));
            card0.AddView(quickOutRow);
            soundHubHomeContainer.AddView(card0);

            // Card 2: 3D & Space (Category 1)
            LinearLayout card1 = BuildSoundHubEssentialCard(
                1,
                S["SoundHub3dSpaceTitle"],
                OneUiIconType.Room3D,
                OneUiTheme.IconSpatial,
                out soundHubSubtitles[1]);
            LinearLayout spaceRow = HRow();
            spaceRow.SetPadding(0, Dp(8), 0, 0);
            Button openStageBtn = PillButton(S["NowPlaying3dStageTab"], () => OpenNowPlayingSheet(subViewIndex: 2));
            Button openSpaceBtn = PillButton(S["SoundHubConfigureBtn"], () => OpenSettingsCategory(1));
            spaceRow.AddView(openStageBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
            spaceRow.AddView(openSpaceBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            card1.AddView(spaceRow);
            soundHubHomeContainer.AddView(card1);

            // Card 3: AI Stems & Vocals (Category 2)
            LinearLayout card2 = BuildSoundHubEssentialCard(
                2,
                S["SoundHubAiStemsTitle"],
                OneUiIconType.Sparkles,
                OneUiTheme.IconAtmosUpmix,
                out soundHubSubtitles[2]);
            LinearLayout aiRow = HRow();
            aiRow.SetPadding(0, Dp(8), 0, 0);
            soundHubQuickAiBtn = PillButton(S["AiPickerLaunchBtn"], () => ShowUnifiedAiModelPickerDialog(RefreshSoundHubCardsUi));
            Button openAiBtn = PillButton(S["SoundHubConfigureBtn"], () => OpenSettingsCategory(2));
            aiRow.AddView(soundHubQuickAiBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f) { RightMargin = Dp(8) });
            aiRow.AddView(openAiBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            card2.AddView(aiRow);
            soundHubHomeContainer.AddView(card2);

            // Card 4: Headphones & Hardware (Category 3)
            LinearLayout card3 = BuildSoundHubEssentialCard(
                3,
                S["SoundHubHeadphonesHwTitle"],
                OneUiIconType.Headphones,
                OneUiTheme.IconStemsCalib,
                out soundHubSubtitles[3]);
            LinearLayout hwRow = HRow();
            hwRow.SetPadding(0, Dp(8), 0, 0);
            Button pickAutoEqBtn = PillButton(S["AutoEqSelectBtn"], ShowAutoEqProfileSelectorDialog);
            Button openHwBtn = PillButton(S["SoundHubConfigureBtn"], () => OpenSettingsCategory(3));
            hwRow.AddView(pickAutoEqBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
            hwRow.AddView(openHwBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            card3.AddView(hwRow);
            soundHubHomeContainer.AddView(card3);

            settingsHubTilesContainer.AddView(soundHubHomeContainer);
        }

        RefreshSoundHubCardsUi();
        return settingsPage;
    }

    LinearLayout BuildSoundHubEssentialCard(
        int categoryIndex,
        string title,
        OneUiIconType icon,
        Color accentColor,
        out TextView subtitleTv) {
        LinearLayout card = Card(title, icon, accentColor);
        subtitleTv = Label(BuildSettingsCategoryLiveSubtitle(categoryIndex), 11.8f, OneUiTheme.TextSub);
        subtitleTv.SetMaxLines(2);
        subtitleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        card.AddView(subtitleTv);
        card.Click += (_, _) => OpenSettingsCategory(categoryIndex);
        return card;
    }

    void RefreshSoundHubCardsUi() {
        if (uiDestroyed) {
            return;
        }
        for (int i = 0; i < soundHubSubtitles.Length; i++) {
            if (soundHubSubtitles[i] != null) {
                soundHubSubtitles[i].Text = BuildSettingsCategoryLiveSubtitle(i);
            }
        }
        if (soundHubQuickSpatialBtn != null) {
            bool active = IsSpeakerXtcOutputActive() || (!IsActiveOutputSpeakerRoute() && store.HeadphoneVirtualizer);
            soundHubQuickSpatialBtn.Text = IsActiveOutputSpeakerRoute()
                ? (store.TransauralXtcEnabled ? S["SpeakerXtcOnShort"] : S["SpeakerXtcOffShort"])
                : (store.HeadphoneVirtualizer ? S["SpatialActiveShort"] : S["SpatialOffShort"]);
            soundHubQuickSpatialBtn.Background = active
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            soundHubQuickSpatialBtn.SetTextColor(active ? OneUiTheme.OnPrimary : ColText);
        }
    }

    /// <summary>
    /// Updates the OTA update indicator dot on the top-right Settings Gear button.
    /// </summary>
    void UpdateSettingsGearOtaBadge(bool hasUpdate) {
        if (settingsGearOtaDot != null) {
            settingsGearOtaDot.Visibility = hasUpdate ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    /// <summary>
    /// Slimmed One UI 9 App Settings Sheet (W55.4.2): displays ONLY app preferences
    /// (Language, Theme/Pro Lab mode, Storage/Engines, Modular Blocks, Updates &amp; About)
    /// and isolates Developer &amp; Diagnostics behind an explicit opt-in Developer sub-page.
    /// </summary>
    void ShowAppSettingsSheet(bool openDeveloperSubPage = false) {
        if (uiDestroyed) {
            return;
        }
        if (openDeveloperSubPage) {
            ShowDeveloperDiagnosticsSheet();
            return;
        }

        List<(LinearLayout card, ViewGroup oldParent)> mountedCards = [];
        LinearLayout sheetBody = new(this) { Orientation = Orientation.Vertical };

        LinearLayout prefCard = Card(S["AppSettingsPrefsTitle"], OneUiIconType.Sliders, OneUiTheme.IconSystemInfo);
        AddSwitchRow(prefCard, S["AppSettingsProLabToggle"], isSettingsProMode, v => {
            SetSettingsProMode(v);
        });
        AddDivider(prefCard);
        LinearLayout devRowWrap = new(this) { Orientation = Orientation.Vertical };
        Button openDevBtn = null;
        AddSwitchRow(prefCard, S["AppSettingsDevModeToggle"], isDeveloperSubPageEnabled, v => {
            isDeveloperSubPageEnabled = v;
            try {
                GetSharedPreferences("cavernize_settings", FileCreationMode.Private)?
                    .Edit()?
                    .PutBoolean("developer_mode_enabled", v)?
                    .Apply();
            } catch { }
            if (openDevBtn != null) {
                openDevBtn.Visibility = v ? ViewStates.Visible : ViewStates.Gone;
            }
        });
        openDevBtn = SecondaryButton(S["AppSettingsOpenDevSubPageBtn"], ShowDeveloperDiagnosticsSheet);
        openDevBtn.Visibility = isDeveloperSubPageEnabled ? ViewStates.Visible : ViewStates.Gone;
        devRowWrap.AddView(openDevBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });
        prefCard.AddView(devRowWrap);
        sheetBody.AddView(prefCard);

        foreach (SettingsCardEntry entry in settingsCardsRegistry) {
            if (entry?.Card == null || entry.CategoryIndex < 4) {
                continue;
            }
            if (ReferenceEquals(entry.Card, diagnosticsDeveloperCardRef)) {
                continue;
            }
            if (entry.IsProOnly && !isSettingsProMode) {
                continue;
            }
            ViewGroup oldParent = entry.Card.Parent as ViewGroup;
            oldParent?.RemoveView(entry.Card);
            entry.Card.Visibility = ViewStates.Visible;
            sheetBody.AddView(entry.Card);
            mountedCards.Add((entry.Card, oldParent));
        }

        RefreshSettingsUpdateUi();
        RefreshFFmpegCardUi();
        RefreshTruehddCardUi();

        void RestoreMountedCards() {
            for (int i = 0; i < mountedCards.Count; i++) {
                var (card, oldParent) = mountedCards[i];
                (card.Parent as ViewGroup)?.RemoveView(card);
                if (oldParent != null) {
                    oldParent.AddView(card);
                }
            }
            RefreshSettingsHubVisibility();
        }

        Android.App.Dialog dlg = OneUiModalDialog.Show(
            this,
            S["TabSettings"],
            S["AppSettingsSheetSub"],
            customContent: sheetBody,
            icon: OneUiIconType.Gear,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"],
            onPositive: RestoreMountedCards);
        if (dlg != null) {
            dlg.DismissEvent += (_, _) => RestoreMountedCards();
        }
    }

    /// <summary>
    /// Isolated opt-in Developer &amp; Diagnostics Sub-Page (Logs, SelfCheck, OmniBridge, CloudLogSync).
    /// </summary>
    void ShowDeveloperDiagnosticsSheet() {
        if (uiDestroyed || diagnosticsDeveloperCardRef == null) {
            return;
        }
        ViewGroup oldParent = diagnosticsDeveloperCardRef.Parent as ViewGroup;
        oldParent?.RemoveView(diagnosticsDeveloperCardRef);
        diagnosticsDeveloperCardRef.Visibility = ViewStates.Visible;
        RefreshLogSummaryLabel();

        LinearLayout wrap = new(this) { Orientation = Orientation.Vertical };
        wrap.AddView(diagnosticsDeveloperCardRef);

        void RestoreDevCard() {
            (diagnosticsDeveloperCardRef.Parent as ViewGroup)?.RemoveView(diagnosticsDeveloperCardRef);
            if (oldParent != null) {
                oldParent.AddView(diagnosticsDeveloperCardRef);
            }
            RefreshSettingsHubVisibility();
        }

        Android.App.Dialog dlg = OneUiModalDialog.Show(
            this,
            S["DevSubPageTitle"],
            S["DevSubPageSub"],
            customContent: wrap,
            icon: OneUiIconType.Logs,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"],
            onPositive: RestoreDevCard);
        if (dlg != null) {
            dlg.DismissEvent += (_, _) => RestoreDevCard();
        }
    }
}
