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
    // FFmpeg bundled NDK engine status
    TextView ffmpegStatusText;
    Button ffmpegCheckBtn;

    // truehdd bundled Dolby TrueHD engine status
    TextView truehddStatusText;
    Button truehddCheckBtn;

    // App version & updater views (inside About section at bottom of Settings)
    TextView updateCurrentVerLabel, updateSettingsStatusText;
    ProgressBar updateSettingsProgressBar;
    Button checkUpdatesBtn, downloadInstallBtn;
    Switch autoCheckUpdatesSwitch;

    // Diagnostics & Log views
    TextView logPathInfoText;
    LinearLayout advContentContainer;
    OneUiIconView advChevron;
    bool isAdvancedExpanded;

    LinearLayout queueListContainer;
    TextView settingsBinauralTitleText, settingsBinauralSubText;
    TextView settingsAutoEqTitleText, settingsAutoEqSubText, settingsAudioDeviceText;
    Switch autoEqEnabledSwitch, autoEqDoubleEqGuardSwitch, autoEqAutoSwitchDeviceSwitch;
    OneUiInlineSelector autoEqTargetSelector;
    Switch lowLatencyAudioSwitch, usbDacNativeSrSwitch;
    Switch headTrackingEnabledSwitch, headTrackingAutoRecenterSwitch, headTrackingLockRollSwitch;
    OneUiInlineSelector headTrackerSourceSelector;
    OneUiSeekBar headTrackingPredictBar;
    TextView audioOutputDiagText, headTrackingStatusText;
    Button headTrackingOscPortBtn;
    Switch proAudioEnabledSwitch, proAudioLiveMicSwitch;
    OneUiInlineSelector proAudioPathSelector, proAudioBufferSelector, proAudioSrSelector, proAudioMicRouteSelector;
    OneUiSeekBar proAudioMicGainBar, proAudioMicMonitorBar;
    TextView proAudioStatusText, proAudioTelemetryText;
    Button proAudioGrantUsbBtn, proAudioScarlettBtn, proAudioVocalChainBtn;
    Switch ismRoomEnabledSwitch, nearFieldEnabledSwitch, airAbsorptionEnabledSwitch;
    OneUiInlineSelector ismRoomPresetSelector, ismWallMaterialSelector, ismOrderSelector;
    OneUiSeekBar ismLevelBar, airTempBar, airHumidityBar;
    TextView externalizationStatusText;
    OneUiInlineSelector magLsModeSelector, magLsCutoffSelector;
    static readonly float[] MagLsCutoffPresets = [0f, 1500f, 2500f, 3500f, 5000f];
    Switch biometricItdSwitch, hptfNotchSwitch;
    OneUiSeekBar headWidthBar, manualItdBar, hptfFreqBar, hptfGainBar, hptfQBar;
    TextView personalizationStatusText;
    Switch iso226EnabledSwitch, iso226TrackVolSwitch, respectDialnormSwitch, dialogEnhanceSwitch;
    OneUiSeekBar iso226RefSplBar, iso226ListenSplBar, dialogBoostBar;
    OneUiInlineSelector loudnessNormSelector, truePeakCeilingSelector;
    TextView loudnessMeterText, loudnessStatusText;
    TextView neuralRuntimeStatusText, neuralPlanSummaryText;
    Button neuralRuntimeCheckBtn, neuralModelsBtn;
    OneUiInlineSelector neuralProviderSelector, neuralOrchestrationSelector, neuralEnsembleSelector, neuralUpmixModeSelector;
    OneUiSeekBar neuralOverlapBar, karaokeVocalLevelBar;
    Switch neuralTtaSwitch, neuralPhaseFixSwitch, neuralConservationSwitch;
    Switch spatialKaraokeSwitch, karaokePreserveBackingSwitch, karaokeLosslessTwinSwitch;
    TextView lgsrStatusText;
    Switch lgsrEnabledSwitch, lgsrAutoDiscoverSwitch, lgsrEnvelopeLockSwitch, lgsrClippingGuardSwitch;
    Switch lgsrBitstreamGuideSwitch, lgsrRejectFakeSwitch, lgsrTier3CleanupSwitch;
    OneUiSeekBar lgsrFusionBar;
    Button lgsrAbBypassBtn;
    CancellationTokenSource calibrationAudioCts;
    int personalizationRebuildSeq;
    Button filtersBtn;
    Switch muteBedSwitch, muteGroundSwitch, speakerVirtSwitch, force24Switch, surroundSwapSwitch, wavSkipSwitch, reportModeSwitch, gradingSwitch;
    Switch pedanticSwitch, unsafeSwitch, keepTempSwitch, normSwitch, limiterOnlySwitch;
    OneUiSeekBar upmixEffectBar, upmixSmoothBar;
    OneUiInlineSelector upmixModeSelector;
    LinearLayout atmosAwareUpmixSection;
    Switch atmosAwareUpmixSwitch;

    sealed class SettingsCardEntry {
        public LinearLayout Card;
        public int CategoryIndex;
        public bool IsProOnly;
        public string ExtraKeywords;
    }

    static readonly int[] NeuralTrackCachePresetsMb = [256, 512, 1024, 2048, 4096];
    static readonly int[] ProAudioSampleRateValues = [0, 44100, 48000, 96000, 192000];
    readonly List<SettingsCardEntry> settingsCardsRegistry = [];
    readonly TextView[] settingsCatSubtitleViews = new TextView[6];
    LinearLayout settingsHubTilesContainer;
    LinearLayout settingsCategoryHeaderBar;
    TextView settingsCategoryHeaderTitle, settingsCategoryHeaderSub, settingsCategoryStatusChip;
    LinearLayout settingsCardsContainer;
    LinearLayout settingsNeuralPrereqCard, settingsUsbPrereqCard, settingsProHiddenCard, settingsSearchEmptyCard;
    EditText settingsSearchInput;
    FrameLayout settingsSearchClearBtn;
    Button settingsModeToggleBtn;
    OneUiInlineSelector neuralTrackCacheCapSelector;
    int activeSettingsCategory = -1;
    string settingsSearchQuery = "";
    bool isSettingsProMode = true;

    void RegisterSettingsCard(LinearLayout card, int categoryIndex, bool isProOnly, string extraKeywords) {
        if (card == null) {
            return;
        }
        settingsCardsRegistry.Add(new SettingsCardEntry {
            Card = card,
            CategoryIndex = categoryIndex,
            IsProOnly = isProOnly,
            ExtraKeywords = extraKeywords ?? ""
        });
    }

    LinearLayout BuildSettingsTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(2), Dp(14), Dp(116));

        isSettingsProMode = GetSharedPreferences("cavernize_settings", FileCreationMode.Private)?.GetBoolean("settings_pro_mode", true) ?? true;
        settingsCardsRegistry.Clear();
        settingsSearchQuery = "";
        activeSettingsCategory = -1;

        // Top Bar: Search Pill (with Clear 'X') + Essential/Pro Mode Toggle Chip
        LinearLayout topRow = HRow();
        topRow.SetPadding(0, Dp(2), 0, Dp(10));

        LinearLayout searchPill = HRow();
        searchPill.SetPadding(Dp(12), Dp(6), Dp(8), Dp(6));
        searchPill.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 999f);
        OneUiIconView srchIco = new(this, OneUiIconType.Search, OneUiTheme.TextMuted);

        settingsSearchInput = new EditText(this) {
            Hint = S["SettingsSearchHint"],
            TextSize = 12.5f,
            ImeOptions = Android.Views.InputMethods.ImeAction.Search
        };
        settingsSearchInput.SetTextColor(ColText);
        settingsSearchInput.SetHintTextColor(ColMuted);
        settingsSearchInput.SetBackgroundColor(Color.Transparent);
        settingsSearchInput.SetSingleLine(true);
        settingsSearchInput.SetPadding(0, Dp(2), 0, Dp(2));

        settingsSearchClearBtn = new FrameLayout(this) {
            Visibility = ViewStates.Gone,
            ContentDescription = S["LibClearSearchDesc"]
        };
        settingsSearchClearBtn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(36, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(settingsSearchClearBtn, 0.88f);
        settingsSearchClearBtn.AddView(new SearchClearIconView(this, OneUiTheme.TextMuted),
            new FrameLayout.LayoutParams(Dp(12), Dp(12)) { Gravity = GravityFlags.Center });
        settingsSearchClearBtn.Click += (_, _) => ClearSettingsSearch();

        settingsSearchInput.TextChanged += (_, _) => {
            string raw = settingsSearchInput.Text?.Trim() ?? "";
            if (settingsSearchClearBtn != null) {
                settingsSearchClearBtn.Visibility = string.IsNullOrEmpty(raw) ? ViewStates.Gone : ViewStates.Visible;
            }
            if (string.Equals(raw, settingsSearchQuery, StringComparison.Ordinal)) {
                return;
            }
            settingsSearchQuery = raw;
            RefreshSettingsHubVisibility();
            SyncPredictiveBackRegistration();
        };

        searchPill.AddView(srchIco, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { RightMargin = Dp(8) });
        searchPill.AddView(settingsSearchInput, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        searchPill.AddView(settingsSearchClearBtn, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        topRow.AddView(searchPill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        settingsModeToggleBtn = PillButton(
            isSettingsProMode ? S["SettingsModeProChip"] : S["SettingsModeEssentialChip"],
            () => SetSettingsProMode(!isSettingsProMode),
            active: isSettingsProMode);
        settingsModeToggleBtn.TextSize = 11.5f;
        settingsModeToggleBtn.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        topRow.AddView(settingsModeToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(topRow);

        // Category Drill-Down & Back Header
        settingsCategoryHeaderBar = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        LinearLayout catNavRow = HRow();
        catNavRow.SetPadding(0, 0, 0, Dp(6));
        Button backToHubBtn = PillButton(S["SettingsBackToHubBtn"], () => {
            ClearSettingsSearch();
            OpenSettingsCategory(-1);
        });
        backToHubBtn.TextSize = 12f;
        catNavRow.AddView(backToHubBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        catNavRow.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1f));
        settingsCategoryStatusChip = OneUiComponents.CreateStatusChip(
            this,
            isSettingsProMode ? S["SettingsModeProChip"] : S["SettingsModeEssentialChip"],
            isSettingsProMode ? OneUiTheme.Primary : OneUiTheme.IconSystemInfo);
        catNavRow.AddView(settingsCategoryStatusChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        settingsCategoryHeaderBar.AddView(catNavRow);

        LinearLayout catTitleWrap = new(this) { Orientation = Orientation.Vertical };
        catTitleWrap.SetPadding(Dp(4), Dp(2), Dp(4), Dp(10));
        settingsCategoryHeaderTitle = Label("", 17.5f, ColText);
        settingsCategoryHeaderTitle.Typeface = OneUiTheme.GetAppleFontBold(this);
        settingsCategoryHeaderTitle.SetIncludeFontPadding(false);
        settingsCategoryHeaderTitle.SetSingleLine(true);
        settingsCategoryHeaderTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        settingsCategoryHeaderSub = Label("", 11.8f, OneUiTheme.TextSub);
        settingsCategoryHeaderSub.SetSingleLine(true);
        settingsCategoryHeaderSub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        settingsCategoryHeaderSub.SetPadding(0, Dp(2), 0, 0);
        catTitleWrap.AddView(settingsCategoryHeaderTitle);
        catTitleWrap.AddView(settingsCategoryHeaderSub);
        settingsCategoryHeaderBar.AddView(catTitleWrap);
        page.AddView(settingsCategoryHeaderBar);

        // One UI 9 6-Category Hub Tiles
        settingsHubTilesContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        TextView hubIntroTv = Label(S["SettingsHubHeaderSub"], 11.8f, OneUiTheme.TextSub);
        hubIntroTv.SetPadding(Dp(4), 0, Dp(4), Dp(8));
        settingsHubTilesContainer.AddView(hubIntroTv);

        (OneUiIconType icon, Color color, string titleKey)[] hubDefs = [
            (OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix, "SettingsCatPlaybackTitle"),
            (OneUiIconType.Headphones, OneUiTheme.IconStemsCalib, "SettingsCatHeadphonesTitle"),
            (OneUiIconType.Sparkles, OneUiTheme.Primary, "SettingsCatAiTitle"),
            (OneUiIconType.Bolt, OneUiTheme.IconSpatial, "SettingsCatProAudioTitle"),
            (OneUiIconType.Wrench, OneUiTheme.IconStudioExport, "SettingsCatEnginesTitle"),
            (OneUiIconType.Info, OneUiTheme.IconSystemInfo, "SettingsCatAboutTitle")
        ];
        for (int i = 0; i < hubDefs.Length; i++) {
            int catIdx = i;
            var def = hubDefs[i];
            LinearLayout tile = OneUiTheme.CreateCategoryHubTile(
                this,
                def.icon,
                def.color,
                S[def.titleKey],
                "",
                () => OpenSettingsCategory(catIdx),
                out TextView subView);
            settingsCatSubtitleViews[catIdx] = subView;
            settingsHubTilesContainer.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(10)
            });
        }
        page.AddView(settingsHubTilesContainer);

        settingsCardsContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        page.AddView(settingsCardsContainer);

        // 1. Spatial 3D Upmixing (DirAC 1024-pt STFT always active internally)
        LinearLayout upmixCard = Card(S["UpmixTitle"], OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix);
        upmixModeSelector = new OneUiInlineSelector(this, S["UpmixModeLabel"]);
        upmixModeSelector.SetItems([S["UpmixModeDirect"], S["UpmixModeSurround"], S["UpmixModeFull3D"]], ResolveUpmixModeIndex());
        upmixModeSelector.SelectionChanged += pos => {
            store.MatrixUpmixing = pos >= 1;
            store.CavernizeUpmix = pos >= 2;
            SyncUpmixModeUi();
            playerEngine?.ApplyUpmixingSettings(upmixingSettings);
            RefreshMusicPlayerControlsUi();
            RefreshSettingsCategorySubtitles();
        };
        upmixCard.AddView(WrapControlWithHelp(upmixModeSelector, S["UpmixModeLabel"].TrimEnd(':'), S["TipUpmixMode"], bottomMarginDp: 2));

        atmosAwareUpmixSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        AddDivider(atmosAwareUpmixSection);
        atmosAwareUpmixSwitch = AddSwitchRow(atmosAwareUpmixSection, S["AtmosAwareUpmix"], store.AtmosAwareUpmix, v => {
            store.AtmosAwareUpmix = v;
            playerEngine?.ApplyUpmixingSettings(upmixingSettings);
            RefreshMusicPlayerControlsUi();
            RefreshSettingsCategorySubtitles();
        }, helpBody: S["TipAtmosAwareUpmix"]);
        atmosAwareUpmixSection.Visibility = (store.MatrixUpmixing || store.CavernizeUpmix) ? ViewStates.Visible : ViewStates.Gone;
        upmixCard.AddView(atmosAwareUpmixSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        AddDivider(upmixCard);
        (upmixEffectBar, _) = AddSliderRow(upmixCard, S["UpmixEffect"], 0, 100, (int)Math.Round(store.UpmixEffect * 100), v => {
            store.UpmixEffect = v * 0.01f;
            playerEngine?.ApplyUpmixingSettings(upmixingSettings);
        }, "%", defaultValue: 75, helpBody: S["TipUpmixEffect"]);
        (upmixSmoothBar, _) = AddSliderRow(upmixCard, S["UpmixSmooth"], 0, 100, (int)Math.Round(store.UpmixSmoothness * 100), v => {
            store.UpmixSmoothness = v * 0.01f;
            playerEngine?.ApplyUpmixingSettings(upmixingSettings);
        }, "%", defaultValue: 80, helpBody: S["TipUpmixSmooth"]);
        Button resetUpmixBtn = PillButton(S["ResetUpmix"], () => {
            store.MatrixUpmixing = false;
            store.CavernizeUpmix = false;
            store.AtmosAwareUpmix = true;
            store.UpmixEffect = 0.75f;
            store.UpmixSmoothness = 0.80f;
            SyncUpmixModeUi();
            atmosAwareUpmixSwitch.Checked = true;
            SyncSlider(upmixEffectBar, 75, 0, 100, "%");
            SyncSlider(upmixSmoothBar, 80, 0, 100, "%");
            playerEngine?.ApplyUpmixingSettings(upmixingSettings);
            RefreshMusicPlayerControlsUi();
            RefreshSettingsCategorySubtitles();
        });
        upmixCard.AddView(resetUpmixBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        RegisterSettingsCard(upmixCard, categoryIndex: 0, isProOnly: false, "upmix matrix cavernize dirac stft atmos spatial 3d surround direct");

        // 2. Room Acoustics, Gain & Dynamic Limiter
        LinearLayout acousticsCard = Card(S["RoomAcousticsTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        normSwitch = AddSwitchRow(acousticsCard, S["NormalizerCheck"], store.NormalizerEnabled, v => {
            store.NormalizerEnabled = v;
            if (playerEngine != null) {
                playerEngine.NormalizerEnabled = v;
            }
            RefreshSettingsCategorySubtitles();
        }, helpBody: S["TipNormalizer"]);
        AddDivider(acousticsCard);
        limiterOnlySwitch = AddSwitchRow(acousticsCard, S["LimiterOnlyCheck"], store.LimiterOnly, v => {
            store.LimiterOnly = v;
            if (playerEngine != null) {
                playerEngine.LimiterOnly = v;
            }
            RefreshSettingsCategorySubtitles();
        });
        AddDivider(acousticsCard);
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
        RegisterSettingsCard(acousticsCard, categoryIndex: 0, isProOnly: false, "room acoustics normalizer limiter gain height dimensions environment");

        // 3. Official Binaural Profile (HRIR / BRIR) & QuickEQ Calibration
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
            S["AutoEqTargetDfNeutral"],
            S["AutoEqTargetBk5128Df"],
            S["AutoEqTargetBk5128Jm1"],
            S["AutoEqTargetHarmanNoBass"],
            S["AutoEqTargetNative"]
        ];
        autoEqTargetSelector.SetItems(targetNames, Math.Clamp(store.AutoEqTargetIndex, 0, targetNames.Length - 1));
        autoEqTargetSelector.SelectionChanged += pos => {
            store.AutoEqTargetIndex = pos;
            ApplyAutoEqRespectingRoute();
            audioDeviceProfileManager?.SaveCurrentSettingsForActiveDevice();
            RefreshAutoEqUi();
        };
        rendCard.AddView(WrapControlWithHelp(autoEqTargetSelector, S["AutoEqTargetTitle"], S["TipAutoEqTarget"], bottomMarginDp: 8));

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
        slot4Card.AddView(headTrackerSourceSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

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

        // Pro-Audio Full-Duplex USB Engine (Path B libusb UAC2 + Path A AAudio Exclusive + Scarlett 2i2)
        LinearLayout proAudioCard = Card(S["ProAudioCardTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        proAudioEnabledSwitch = AddSwitchRow(proAudioCard, S["ProAudioEnableTitle"], store.ProAudioEnabled, v => {
            store.ProAudioEnabled = v;
            ApplyAndRefreshProAudio();
        }, helpBody: S["TipProAudioEngine"]);

        proAudioStatusText = Label("", 11.4f, OneUiTheme.TextPrimary);
        proAudioStatusText.SetSingleLine(true);
        proAudioStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        proAudioCard.AddView(proAudioStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(2)
        });

        proAudioTelemetryText = Label("", 10.8f, OneUiTheme.TextMuted);
        proAudioTelemetryText.SetSingleLine(true);
        proAudioTelemetryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        proAudioCard.AddView(proAudioTelemetryText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        proAudioPathSelector = new OneUiInlineSelector(this, S["ProAudioPathTitle"]);
        string[] proAudioPathLabels = [
            S["ProAudioPathAuto"],
            S["ProAudioPathB"],
            S["ProAudioPathA"],
            S["ProAudioPathLegacy"]
        ];
        proAudioPathSelector.SetItems(proAudioPathLabels, Math.Clamp(store.ProAudioPreferredPathIndex, 0, proAudioPathLabels.Length - 1));
        proAudioPathSelector.SelectionChanged += pos => {
            store.ProAudioPreferredPathIndex = pos;
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(proAudioPathSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        proAudioBufferSelector = new OneUiInlineSelector(this, S["ProAudioBufferTitle"]);
        string[] proAudioBufLabels = [S["ProAudioBuf64"], S["ProAudioBuf128"], S["ProAudioBuf256"]];
        int initialBufIdx = store.ProAudioBufferFrames <= 64 ? 0 : (store.ProAudioBufferFrames <= 128 ? 1 : 2);
        proAudioBufferSelector.SetItems(proAudioBufLabels, initialBufIdx);
        proAudioBufferSelector.SelectionChanged += pos => {
            store.ProAudioBufferFrames = pos == 0 ? 64 : (pos == 1 ? 128 : 256);
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(proAudioBufferSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        proAudioSrSelector = new OneUiInlineSelector(this, S["ProAudioSampleRateTitle"]);
        string[] proAudioSrLabels = [S["ProAudioSrAuto"], "44.1 kHz", "48 kHz", "96 kHz", "192 kHz"];
        int initialSrIdx = Array.IndexOf(ProAudioSampleRateValues, store.ProAudioLockedSampleRate);
        proAudioSrSelector.SetItems(proAudioSrLabels, initialSrIdx >= 0 ? initialSrIdx : 0);
        proAudioSrSelector.SelectionChanged += pos => {
            store.ProAudioLockedSampleRate = ProAudioSampleRateValues[Math.Clamp(pos, 0, ProAudioSampleRateValues.Length - 1)];
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(proAudioSrSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddDivider(proAudioCard);
        proAudioLiveMicSwitch = AddSwitchRow(proAudioCard, S["ProAudioLiveMicTitle"], store.LiveMicEnabled, v => {
            store.LiveMicEnabled = v;
            if (v) {
                RequestRecordAudioPermissionIfNeeded();
                if (!store.ProAudioEnabled) {
                    store.ProAudioEnabled = true;
                }
            }
            ApplyAndRefreshProAudio();
        }, helpBody: S["TipProAudioLiveMic"]);

        proAudioMicRouteSelector = new OneUiInlineSelector(this, S["ProAudioMicRouteTitle"]);
        string[] micRouteLabels = [
            S["ProAudioMicOff"],
            S["ProAudioMicCh1"],
            S["ProAudioMicCh2"],
            S["ProAudioMicStereo"]
        ];
        proAudioMicRouteSelector.SetItems(micRouteLabels, Math.Clamp(store.LiveMicRoutingIndex, 0, micRouteLabels.Length - 1));
        proAudioMicRouteSelector.SelectionChanged += pos => {
            store.LiveMicRoutingIndex = pos;
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(proAudioMicRouteSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        (proAudioMicGainBar, _) = AddSliderRow(
            proAudioCard,
            S["ProAudioMicGainTitle"],
            -18,
            24,
            Math.Clamp((int)MathF.Round(store.LiveMicInputGainDb), -18, 24),
            v => {
                store.LiveMicInputGainDb = v;
                ApplyAndRefreshProAudio();
            },
            " dB",
            defaultValue: 0);

        (proAudioMicMonitorBar, _) = AddSliderRow(
            proAudioCard,
            S["ProAudioMicMonitorTitle"],
            0,
            125,
            Math.Clamp((int)MathF.Round(store.LiveMicMonitorLevel * 100f), 0, 125),
            v => {
                store.LiveMicMonitorLevel = v * 0.01f;
                ApplyAndRefreshProAudio();
            },
            "%",
            defaultValue: 85);

        LinearLayout proAudioRow1 = HRow();
        proAudioGrantUsbBtn = PillButton(S["ProAudioGrantUsbBtn"], () => {
            if (usbProAudioManager != null && usbProAudioManager.RequestUsbPermission()) {
                ShowToast(S["ProAudioUsbRequestedToast"]);
            } else {
                ShowToast(S["ProAudioUsbNoDeviceToast"]);
            }
            RefreshProAudioUi();
        }, active: true);
        Button calibrateRttBtn = PillButton(S["ProAudioCalibrateRttBtn"], ShowCalibrateRttModal);
        proAudioRow1.AddView(proAudioGrantUsbBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(8) });
        proAudioRow1.AddView(calibrateRttBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.95f));
        proAudioCard.AddView(proAudioRow1, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        LinearLayout proAudioRow2 = HRow();
        proAudioScarlettBtn = PillButton(S["ScarlettControlsBtn"], ShowScarlett2i2ControlsModal);
        proAudioVocalChainBtn = PillButton(S["ProAudioVocalChainBtn"], ShowAutoVocalMixMatchModal, active: true);
        proAudioRow2.AddView(proAudioScarlettBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        proAudioRow2.AddView(proAudioVocalChainBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        proAudioCard.AddView(proAudioRow2, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout proAudioRow3 = HRow();
        Button proAudioCreditsBtn = PillButton(S["ProAudioCreditsBtn"], ShowProAudioCreditsModal);
        proAudioRow3.AddView(proAudioCreditsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        proAudioCard.AddView(proAudioRow3, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        RegisterSettingsCard(proAudioCard, categoryIndex: 3, isProOnly: true, "pro audio usb aaudio libusb uac2 microphone live mic vocal mix match focusrite scarlett 2i2 4i4 rtt");

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

        iso226TrackVolSwitch = AddSwitchRow(slot6bCard, S["Iso226TrackVolTitle"], store.Iso226TrackVolume, v => {
            store.Iso226TrackVolume = v;
            ApplyAndRefreshLoudness();
        });

        (iso226RefSplBar, _) = AddSliderRow(
            slot6bCard,
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
            slot6bCard,
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
        slot6bCard.AddView(loudnessNormSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        respectDialnormSwitch = AddSwitchRow(slot6bCard, S["RespectDialnormTitle"], store.RespectDolbyDialnorm, v => {
            store.RespectDolbyDialnorm = v;
            ApplyAndRefreshLoudness();
        }, helpBody: S["TipRespectDialnorm"]);

        AddDivider(slot6bCard);

        dialogEnhanceSwitch = AddSwitchRow(slot6bCard, S["DialogEnhanceTitle"], store.DialogEnhancementEnabled, v => {
            store.DialogEnhancementEnabled = v;
            ApplyAndRefreshLoudness();
        }, helpBody: S["TipDialogEnhance"]);

        (dialogBoostBar, _) = AddSliderRow(
            slot6bCard,
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
        slot6bCard.AddView(truePeakCeilingSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        Button resetLoudnessMeterBtn = PillButton(S["LoudnessResetMeterBtn"], () => {
            playerEngine?.ResetLoudnessMeter();
            RefreshLiveLoudnessMeterUi();
            ShowToast(S["LoudnessMeterResetToast"]);
        });
        slot6bCard.AddView(resetLoudnessMeterBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4)
        });

        RegisterSettingsCard(slot6bCard, categoryIndex: 0, isProOnly: true, "psychoacoustics loudness iso 226 phon ebu r128 lufs dialnorm dialog boost true peak ceiling");

        // On-Device Neural Audio Runtime (ONNX), MVSEP Anti-Artifact & Spatial Karaoke
        LinearLayout slot2bCard = Card();
        LinearLayout slot2bHeader = HRow();
        slot2bHeader.SetPadding(0, 0, 0, Dp(10));
        slot2bHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix));
        TextView slot2bTitleTv = SectionTitle(S["Slot2BCardTitle"]);
        slot2bTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        slot2bHeader.AddView(slot2bTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        slot2bHeader.AddView(CreateInlineHelpBtn(S["Slot2BCardTitle"], S["TipNeuralRuntime"]), new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        slot2bCard.AddView(slot2bHeader);

        neuralRuntimeStatusText = Label(S["NeuralRuntimeNotInstalled"], 12f, OneUiTheme.TextSub);
        neuralRuntimeStatusText.SetPadding(0, 0, 0, Dp(8));
        slot2bCard.AddView(neuralRuntimeStatusText);

        neuralRuntimeCheckBtn = PillButton(S["NeuralRuntimeCheckBtn"], () => _ = CheckOnnxRuntimeUpdateAsync(silent: false));
        slot2bCard.AddView(neuralRuntimeCheckBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        neuralModelsBtn = PillButton(string.Format(S["NeuralModelsBtn"], 0), ShowNeuralModelsModal);
        slot2bCard.AddView(neuralModelsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8),
            BottomMargin = Dp(6)
        });

        AddDivider(slot2bCard);

        neuralProviderSelector = new OneUiInlineSelector(this, S["NeuralProviderTitle"]);
        string[] providerLabels = [
            S["NeuralProviderAuto"],
            S["NeuralProviderQnn"],
            S["NeuralProviderXnnpack"],
            S["NeuralProviderCpu"]
        ];
        neuralProviderSelector.SetItems(providerLabels, Math.Clamp(store.NeuralProviderIndex, 0, providerLabels.Length - 1));
        neuralProviderSelector.SelectionChanged += pos => {
            store.NeuralProviderIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        slot2bCard.AddView(neuralProviderSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        neuralOrchestrationSelector = new OneUiInlineSelector(this, S["NeuralOrchestrationTitle"]);
        string[] orchLabels = [
            S["NeuralOrchestrationAuto"],
            S["NeuralOrchestrationManual"]
        ];
        neuralOrchestrationSelector.SetItems(orchLabels, Math.Clamp(store.NeuralOrchestrationModeIndex, 0, orchLabels.Length - 1));
        neuralOrchestrationSelector.SelectionChanged += pos => {
            store.NeuralOrchestrationModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        slot2bCard.AddView(WrapControlWithHelp(neuralOrchestrationSelector, S["NeuralOrchestrationTitle"], S["TipNeuralOrchestration"], bottomMarginDp: 4));

        neuralPlanSummaryText = Label("", 11.5f, OneUiTheme.TextSub);
        neuralPlanSummaryText.SetSingleLine(true);
        neuralPlanSummaryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        neuralPlanSummaryText.SetPadding(Dp(2), 0, Dp(2), Dp(6));
        slot2bCard.AddView(neuralPlanSummaryText);

        neuralTrackCacheCapSelector = new OneUiInlineSelector(this, S["NeuralTrackCacheCapTitle"]);
        string[] cacheCapLabels = ["256 MB", "512 MB", "1024 MB", "2048 MB", "4096 MB"];
        neuralTrackCacheCapSelector.SetItems(cacheCapLabels, ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb));
        neuralTrackCacheCapSelector.SelectionChanged += pos => {
            int mb = NeuralTrackCachePresetsMb[Math.Clamp(pos, 0, NeuralTrackCachePresetsMb.Length - 1)];
            ApplyNeuralTrackCacheMaxMb(mb);
        };
        slot2bCard.AddView(neuralTrackCacheCapSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        (neuralOverlapBar, _) = AddSliderRow(
            slot2bCard,
            S["NeuralOverlapTitle"],
            2,
            8,
            Math.Clamp(store.NeuralOverlapFactor, 2, 8),
            v => {
                store.NeuralOverlapFactor = v;
                if (neuralOverlapBar != null) {
                    neuralOverlapBar.ValueText = $"{v}x";
                    neuralOverlapBar.Invalidate();
                }
                ApplyAndRefreshNeuralAudio();
            },
            "x",
            defaultValue: 4,
            helpBody: S["TipMvsepOverlap"]);
        if (neuralOverlapBar != null) {
            neuralOverlapBar.ValueText = $"{store.NeuralOverlapFactor}x";
        }

        neuralTtaSwitch = AddSwitchRow(slot2bCard, S["NeuralTtaTitle"], store.NeuralTtaEnabled, v => {
            store.NeuralTtaEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipMvsepTta"]);
        AddDivider(slot2bCard);

        neuralEnsembleSelector = new OneUiInlineSelector(this, S["NeuralEnsembleTitle"]);
        string[] ensembleLabels = [
            S["NeuralEnsembleAvgWave"],
            S["NeuralEnsembleMinFft"],
            S["NeuralEnsembleAvgFft"]
        ];
        neuralEnsembleSelector.SetItems(ensembleLabels, Math.Clamp(store.NeuralEnsembleModeIndex, 0, ensembleLabels.Length - 1));
        neuralEnsembleSelector.SelectionChanged += pos => {
            store.NeuralEnsembleModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        slot2bCard.AddView(neuralEnsembleSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        neuralPhaseFixSwitch = AddSwitchRow(slot2bCard, S["NeuralPhaseFixTitle"], store.NeuralPhaseFixEnabled, v => {
            store.NeuralPhaseFixEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipMvsepPhaseFix"]);
        AddDivider(slot2bCard);

        neuralConservationSwitch = AddSwitchRow(slot2bCard, S["NeuralConservationTitle"], store.NeuralEnergyConservation, v => {
            store.NeuralEnergyConservation = v;
            ApplyAndRefreshNeuralAudio();
        });
        AddDivider(slot2bCard);

        spatialKaraokeSwitch = AddSwitchRow(slot2bCard, S["SpatialKaraokeTitle"], store.SpatialKaraokeEnabled, v => {
            store.SpatialKaraokeEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipSpatialKaraoke"]);

        (karaokeVocalLevelBar, _) = AddSliderRow(
            slot2bCard,
            S["KaraokeVocalLevelTitle"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100),
            v => {
                store.KaraokeVocalLevel = v * 0.01f;
                playerEngine?.SetKaraokeVocalLevel(store.KaraokeVocalLevel);
                RefreshLyricsSingDockUi();
                RefreshStudioW12ContextUi();
            },
            "%",
            defaultValue: 100);

        karaokePreserveBackingSwitch = AddSwitchRow(slot2bCard, S["KaraokePreserveBackingTitle"], store.KaraokePreserveBackingVocals, v => {
            store.KaraokePreserveBackingVocals = v;
            ApplyAndRefreshNeuralAudio();
            RefreshStudioW12ContextUi();
        });
        AddDivider(slot2bCard);

        karaokeLosslessTwinSwitch = AddSwitchRow(slot2bCard, S["KaraokeLosslessTwinTitle"], store.UseLosslessTwinForKaraoke, v => {
            store.UseLosslessTwinForKaraoke = v;
            ApplyAndRefreshNeuralAudio();
        });
        AddDivider(slot2bCard);

        neuralUpmixModeSelector = new OneUiInlineSelector(this, S["NeuralUpmixModeTitle"]);
        string[] upmixModeLabels = [
            S["NeuralUpmixParametric"],
            S["NeuralUpmixStems3D"],
            S["NeuralUpmixHybrid714"]
        ];
        neuralUpmixModeSelector.SetItems(upmixModeLabels, Math.Clamp(store.NeuralSpatialUpmixModeIndex, 0, upmixModeLabels.Length - 1));
        neuralUpmixModeSelector.SelectionChanged += pos => {
            store.NeuralSpatialUpmixModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
            RefreshNeuralPrepUi();
            if (pos is 1 or 2 &&
                playerEngine?.NeuralStage?.HasPlayableSeparatedStems != true &&
                !isNeuralPrepRunning &&
                GetActiveTrack() != null) {
                PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, silentIfNotReady: true);
            }
        };
        slot2bCard.AddView(WrapControlWithHelp(neuralUpmixModeSelector, S["NeuralUpmixModeTitle"], S["TipNeuralUpmix"], bottomMarginDp: 2));

        RegisterSettingsCard(slot2bCard, categoryIndex: 2, isProOnly: false, "ai neural onnx runtime models orchestrator cache mb mvsep stems karaoke qnn xnnpack");

        // Universal Lossless Twin & Lossless-Guided Atmos Restoration (LGSR)
        LinearLayout slot2cCard = Card();
        LinearLayout slot2cHeader = HRow();
        slot2cHeader.SetPadding(0, 0, 0, Dp(10));
        slot2cHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sliders, OneUiTheme.IconSystemInfo));
        TextView slot2cTitleTv = SectionTitle(S["Slot2CCardTitle"]);
        slot2cTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        slot2cHeader.AddView(slot2cTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        slot2cHeader.AddView(CreateInlineHelpBtn(S["Slot2CCardTitle"], S["TipSlot2CLgsr"]), new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        slot2cCard.AddView(slot2cHeader);

        lgsrStatusText = Label(S["LgsrStatusNone"], 12f, OneUiTheme.TextSub);
        lgsrStatusText.SetPadding(0, 0, 0, Dp(8));
        slot2cCard.AddView(lgsrStatusText);

        lgsrEnabledSwitch = AddSwitchRow(slot2cCard, S["LgsrEnabledTitle"], store.LgsrEnabled, v => {
            store.LgsrEnabled = v;
            ApplyAndRefreshLgsr(rescanTwin: v);
        });
        AddDivider(slot2cCard);

        lgsrAutoDiscoverSwitch = AddSwitchRow(slot2cCard, S["LgsrAutoDiscoverTitle"], store.LgsrAutoDiscoverTwin, v => {
            store.LgsrAutoDiscoverTwin = v;
            ApplyAndRefreshLgsr(rescanTwin: v);
        });
        AddDivider(slot2cCard);

        (lgsrFusionBar, _) = AddSliderRow(
            slot2cCard,
            S["LgsrFusionStrengthTitle"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.LgsrFusionStrength * 100f), 0, 100),
            v => {
                store.LgsrFusionStrength = v * 0.01f;
                ApplyAndRefreshLgsr(rescanTwin: false);
            },
            "%",
            defaultValue: 85);

        lgsrEnvelopeLockSwitch = AddSwitchRow(slot2cCard, S["LgsrEnvelopeLockTitle"], store.LgsrDynamicEnvelopeLock, v => {
            store.LgsrDynamicEnvelopeLock = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrEnvelopeLock"]);
        AddDivider(slot2cCard);

        lgsrClippingGuardSwitch = AddSwitchRow(slot2cCard, S["LgsrClippingGuardTitle"], store.LgsrClippingGuard, v => {
            store.LgsrClippingGuard = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrClippingGuard"]);
        AddDivider(slot2cCard);

        lgsrBitstreamGuideSwitch = AddSwitchRow(slot2cCard, S["LgsrBitstreamGuideTitle"], store.LgsrBitstreamGuide, v => {
            store.LgsrBitstreamGuide = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrBitstreamGuide"]);
        AddDivider(slot2cCard);

        lgsrRejectFakeSwitch = AddSwitchRow(slot2cCard, S["LgsrRejectFakeTitle"], store.LgsrRejectFakeLossless, v => {
            store.LgsrRejectFakeLossless = v;
            ApplyAndRefreshLgsr(rescanTwin: true);
        });
        AddDivider(slot2cCard);

        lgsrTier3CleanupSwitch = AddSwitchRow(slot2cCard, S["LgsrTier3CleanupTitle"], store.LgsrTier3ResidualCleanup, v => {
            store.LgsrTier3ResidualCleanup = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrTier3Cleanup"]);

        LinearLayout lgsrBtns = HRow();
        lgsrAbBypassBtn = PillButton(S["LgsrAbToggleBtn"], () => {
            if (playerEngine != null) {
                playerEngine.LgsrBypassed = !playerEngine.LgsrBypassed;
                RefreshLgsrUi();
                RefreshSpatialXRayHud();
            }
        });
        Button lgsrRescanBtn = PillButton(S["LgsrRescanTwinBtn"], () => {
            CavernizeTrack active = GetActiveTrack();
            if (active != null) {
                StartLosslessTwinDiscoveryAsync(active, forceRescan: true);
            } else {
                RefreshLgsrUi();
            }
        });
        Button lgsrLicBtn = PillButton(S["LgsrLicensesBtn"], ShowLgsrLicensesModal);
        lgsrBtns.AddView(lgsrAbBypassBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(6) });
        lgsrBtns.AddView(lgsrRescanBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(6) });
        lgsrBtns.AddView(lgsrLicBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.90f));
        slot2cCard.AddView(lgsrBtns, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(10)
        });

        RegisterSettingsCard(slot2cCard, categoryIndex: 2, isProOnly: true, "lgsr lossless twin restoration atmos eac3 flac fusion envelope lock tier3");

        // Collapsible Advanced Rendering & CLI Options
        LinearLayout advCard = Card();
        LinearLayout advHeader = HRow();
        OneUiTheme.AttachRecoil(advHeader, 0.98f);
        advHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Wrench, OneUiTheme.IconStudioExport, 32));
        TextView advTitleTv = SectionTitle(S["AdvancedTitle"]);
        advTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        advHeader.AddView(advTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        advChevron = new OneUiIconView(this, isAdvancedExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown, OneUiTheme.PeachText);
        advHeader.AddView(advChevron, new LinearLayout.LayoutParams(Dp(18), Dp(18)));
        advCard.AddView(advHeader);

        advContentContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isAdvancedExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        advContentContainer.SetPadding(0, Dp(12), 0, 0);
        advHeader.Click += (_, _) => {
            isAdvancedExpanded = !isAdvancedExpanded;
            SetExpandedWithAnimation(advContentContainer, isAdvancedExpanded);
            advChevron.IconType = isAdvancedExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
        };

        muteBedSwitch = AddSwitchRow(advContentContainer, S["MuteBed"], store.MuteBed, v => {
            store.MuteBed = v;
            if (playerEngine != null) {
                playerEngine.MuteBed = v;
            }
            refreshBentoTilesUi?.Invoke();
        }, helpBody: S["TipMuteBed"]);
        AddDivider(advContentContainer);
        muteGroundSwitch = AddSwitchRow(advContentContainer, S["MuteGround"], store.MuteGround, v => {
            store.MuteGround = v;
            if (playerEngine != null) {
                playerEngine.MuteGround = v;
            }
            refreshBentoTilesUi?.Invoke();
        }, helpBody: S["TipMuteGround"]);
        AddDivider(advContentContainer);
        force24Switch = AddSwitchRow(advContentContainer, S["Force24Bit"], store.Force24Bit, v => store.Force24Bit = v, helpBody: S["TipForce24Bit"]);
        AddDivider(advContentContainer);
        surroundSwapSwitch = AddSwitchRow(advContentContainer, S["SurroundSwap"], store.SurroundSwap, v => store.SurroundSwap = v, helpBody: S["TipSurroundSwap"]);
        AddDivider(advContentContainer);
        wavSkipSwitch = AddSwitchRow(advContentContainer, S["WavSkip"], store.WavChannelSkip, v => store.WavChannelSkip = v, helpBody: S["TipWavSkip"]);
        AddDivider(advContentContainer);
        reportModeSwitch = AddSwitchRow(advContentContainer, S["ReportMode"], store.ReportOnlyMode, v => store.ReportOnlyMode = v, helpBody: S["TipReportMode"]);
        AddDivider(advContentContainer);
        gradingSwitch = AddSwitchRow(advContentContainer, S["QualityGrading"], store.QualityGrading, v => store.QualityGrading = v, helpBody: S["TipQualityGrading"]);
        AddDivider(advContentContainer);

        AddSliderRow(advContentContainer, S["MlpPres"], -1, 4, store.MlpPresentation, v => {
            store.MlpPresentation = v;
            store.ApplyGlobals();
            if (decodedTrueHDTrack != null && !isPreparingTrueHD) {
                ClearDecodedTrueHDState();
                BindActiveTrackToUiAndPlayer(selectedTrack);
            }
        }, "", useBlue: true, defaultValue: -1, helpBody: S["TipMlpPres"]);

        OneUiInlineSelector qualSelector = new(this, S["AudioQuality"]);
        string[] qualNames = Enum.GetNames(typeof(QualityModes));
        qualSelector.SetItems(qualNames, Math.Clamp(store.AudioQualityIndex, 0, qualNames.Length - 1));
        qualSelector.SelectionChanged += pos => {
            store.AudioQualityIndex = pos;
            if (playerEngine != null) {
                playerEngine.AudioQuality = (QualityModes)pos;
            }
            RefreshSettingsCategorySubtitles();
        };
        advContentContainer.AddView(qualSelector);

        AddDivider(advContentContainer);
        pedanticSwitch = AddSwitchRow(advContentContainer, S["Pedantic"], store.Pedantic, v => { store.Pedantic = v; store.ApplyGlobals(); }, helpBody: S["TipPedantic"]);
        AddDivider(advContentContainer);
        unsafeSwitch = AddSwitchRow(advContentContainer, S["Unsafe"], store.Unsafe, v => { store.Unsafe = v; store.ApplyGlobals(); }, helpBody: S["TipUnsafe"]);
        AddDivider(advContentContainer);
        keepTempSwitch = AddSwitchRow(advContentContainer, S["KeepTemp"], store.KeepTempFiles, v => { store.KeepTempFiles = v; store.ApplyGlobals(); });

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) {
            Button permBtn = PillButton(S["StoragePermBtn"], RequestAllFilesAccess);
            advContentContainer.AddView(permBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        }

        advCard.AddView(advContentContainer);
        RegisterSettingsCard(advCard, categoryIndex: 4, isProOnly: true, "advanced cli render mute bed ground 24-bit surround swap wav report grading mlp quality pedantic unsafe temp storage permission");

        // FFmpeg Audio Engine (Bundled W^X NDK in NativeLibraryDir)
        LinearLayout ffCard = Card(S["FFmpegCardTitle"], OneUiIconType.Gear, OneUiTheme.IconSliderMedia);
        ffmpegStatusText = Label(S["FFmpegNotInstalled"], 12.5f, OneUiTheme.TextSub);
        ffmpegStatusText.SetPadding(0, 0, 0, Dp(8));
        ffCard.AddView(ffmpegStatusText);

        ffmpegCheckBtn = PillButton(S["FFmpegCheckBtn"], () => _ = CheckFFmpegUpdateAsync(silent: false));
        ffCard.AddView(ffmpegCheckBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        AddDivider(ffCard);
        ffCard.AddView(WrapControlWithHelp(Label(S["FFmpegArgsLabel"], 12f, ColMuted), S["FFmpegCardTitle"], S["TipFFmpegArgs"], bottomMarginDp: 2));
        EditText ffArgsBox = new(this) { Text = store.FFmpegExtraArgs, TextSize = 12.5f };
        ffArgsBox.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
        ffArgsBox.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        ffArgsBox.SetTextColor(ColText);
        ffArgsBox.TextChanged += (_, _) => {
            store.FFmpegExtraArgs = ffArgsBox.Text;
            ParseFFmpegExtraArgs(ffArgsBox.Text);
        };
        ffCard.AddView(ffArgsBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        RegisterSettingsCard(ffCard, categoryIndex: 4, isProOnly: false, "ffmpeg engine audio transcode container args");

        // truehdd Dolby TrueHD Engine (Bundled W^X NativeLibraryDir)
        LinearLayout thdCard = Card();
        LinearLayout thdHeader = HRow();
        thdHeader.SetPadding(0, 0, 0, Dp(10));
        thdHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix));
        TextView thdTitleTv = SectionTitle(S["TruehddCardTitle"]);
        thdTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        thdHeader.AddView(thdTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        thdHeader.AddView(CreateInlineHelpBtn(S["TruehddCardTitle"], S["TipTruehdd"]), new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        thdCard.AddView(thdHeader);

        truehddStatusText = Label(S["TruehddNotInstalled"], 12.5f, OneUiTheme.TextSub);
        truehddStatusText.SetPadding(0, 0, 0, Dp(8));
        thdCard.AddView(truehddStatusText);

        truehddCheckBtn = PillButton(S["TruehddCheckBtn"], () => _ = CheckTruehddUpdateAsync(silent: false));
        thdCard.AddView(truehddCheckBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        RegisterSettingsCard(thdCard, categoryIndex: 4, isProOnly: false, "truehdd dolby truehd atmos mlp decoder engine");

        // Diagnostics & Shared Logs
        LinearLayout logCard = Card(S["DiagnosticsTitle"], OneUiIconType.Logs, OneUiTheme.IconSystemInfo);
        logPathInfoText = Label("", 11.5f, ColMuted);
        logPathInfoText.SetPadding(0, 0, 0, Dp(8));
        RefreshLogSummaryLabel();
        logCard.AddView(logPathInfoText);

        LinearLayout logBtns = HRow();
        Button viewLogBtn = PillButton(S["ViewLogBtn"], () => {
            ShowMessageDialog(S["DiagnosticsTitle"], AppLogger.ReadAll(), OneUiIconType.Logs, monospace: true);
        }, active: true);
        Button exportLogBtn = PillButton(S["ExportLogBtn"], () => {
            string logPath = AppLogger.SharedLogPath;
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath)) {
                ShareLocalFile(logPath);
            } else {
                Toast.MakeText(this, S["LogFileNotAvailableToast"], ToastLength.Short).Show();
            }
        });
        Button clearLogBtn = PillButton(S["ClearLogBtn"], () => {
            AppLogger.Clear(this);
            RefreshLogSummaryLabel();
            Toast.MakeText(this, S["LogClearedMsg"], ToastLength.Short).Show();
        });
        logBtns.AddView(viewLogBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        logBtns.AddView(exportLogBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        logBtns.AddView(clearLogBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        logCard.AddView(logBtns);
        RegisterSettingsCard(logCard, categoryIndex: 5, isProOnly: false, "diagnostics logs error debug export clear");

        // Language, About & App Version Updater
        LinearLayout helpCard = Card(S["AboutVerSection"], OneUiIconType.Info, OneUiTheme.IconSystemInfo);
        OneUiInlineSelector langSelector = new(this, S["LanguageLabel"]);
        string[] langLabels = [S["LangAutoSystem"], S["LangPtBr"], S["LangEnUs"], S["LangHuHu"], S["LangZhCn"]];
        string[] langCodes = ["", "pt-BR", "en-US", "hu-HU", "zh-CN"];
        int curLangIdx = Array.IndexOf(langCodes, store.LanguageCode ?? "");
        langSelector.SetItems(langLabels, Math.Max(0, curLangIdx));
        langSelector.SelectionChanged += pos => {
            string chosen = langCodes[pos];
            if (!string.Equals(store.LanguageCode ?? "", chosen, StringComparison.Ordinal)) {
                ApplyLanguage(chosen);
            }
        };
        helpCard.AddView(langSelector);

        LinearLayout helpBtns = HRow();
        helpBtns.SetPadding(0, Dp(10), 0, Dp(4));
        Button guideBtn = PillButton(S["UserGuideBtn"], () => {
            Intent browser = new(Intent.ActionView, Android.Net.Uri.Parse("https://cavern.sbence.hu/cavern/doc.php?p=Cavernize"));
            StartActivity(browser);
        });
        Button aboutBtn = PillButton(S["AboutBtn"], () => {
            (string verName, long verCode) = AppUpdateService.GetInstalledVersion(this);
            string themeLine = OneUiTheme.IsDynamicWallpaperActive ? S["AboutThemeDynamicWallpaper"] : S["AboutThemeOneUiBlue"];
            string engineLine = CavernAmp.Available ? S["AboutEngineCavernAmpActive"] : S["AboutEngineManagedArm64"];
            string msg = string.Format(S["AboutDialogBodyFmt"], Listener.Info, verName, verCode, themeLine, engineLine);
            ShowMessageDialog(S["AboutBtn"], msg, OneUiIconType.Info);
        });
        helpBtns.AddView(guideBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        helpBtns.AddView(aboutBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        helpCard.AddView(helpBtns);

        AddDivider(helpCard);
        (string curVer, long buildNo) = AppUpdateService.GetInstalledVersion(this);
        updateCurrentVerLabel = Label(string.Format(S["CurrentVersionLabel"], $"{curVer} (build {buildNo})"), 13f, ColText);
        updateCurrentVerLabel.SetTypeface(null, TypefaceStyle.Bold);
        helpCard.AddView(updateCurrentVerLabel);

        updateSettingsStatusText = Label(S["Ready"], 12f, OneUiTheme.IconSystemInfo);
        updateSettingsStatusText.SetPadding(0, Dp(2), 0, Dp(6));
        helpCard.AddView(updateSettingsStatusText);

        updateSettingsProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0,
            Visibility = ViewStates.Gone
        };
        OneUiTheme.StyleProgressBar(this, updateSettingsProgressBar);
        helpCard.AddView(updateSettingsProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)) { BottomMargin = Dp(8) });

        LinearLayout updBtns = HRow();
        checkUpdatesBtn = PillButton(S["CheckUpdateBtn"], () => _ = CheckForUpdatesAsync(silent: false));
        downloadInstallBtn = PrimaryButton(S["DownloadInstallBtn"], StartUpdateDownloadAndInstall);
        downloadInstallBtn.Visibility = ViewStates.Gone;
        updBtns.AddView(checkUpdatesBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        updBtns.AddView(downloadInstallBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.2f) { LeftMargin = Dp(8) });
        helpCard.AddView(updBtns);

        AddDivider(helpCard);
        autoCheckUpdatesSwitch = AddSwitchRow(helpCard, S["AutoCheckSwitch"], store.AutoCheckUpdates, v => store.AutoCheckUpdates = v);
        RegisterSettingsCard(helpCard, categoryIndex: 5, isProOnly: false, "language about version ota update guide credits");

        // Assemble Category Cards & Prerequisite States in Category Order (0..5)
        settingsNeuralPrereqCard = OneUiComponents.CreateEmptyPrereqCard(
            this,
            S["SettingsPrereqNeuralTitle"],
            S["SettingsPrereqNeuralSub"],
            S["SettingsPrereqNeuralCta"],
            ShowNeuralModelsModal);
        settingsUsbPrereqCard = OneUiComponents.CreateEmptyPrereqCard(
            this,
            S["SettingsPrereqUsbTitle"],
            S["SettingsPrereqUsbSub"],
            S["ProAudioGrantUsbBtn"],
            () => proAudioGrantUsbBtn?.PerformClick());

        for (int cat = 0; cat < 6; cat++) {
            if (cat == 2) {
                settingsCardsContainer.AddView(settingsNeuralPrereqCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            } else if (cat == 3) {
                settingsCardsContainer.AddView(settingsUsbPrereqCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            }
            foreach (SettingsCardEntry entry in settingsCardsRegistry) {
                if (entry.CategoryIndex == cat && entry.Card != null) {
                    settingsCardsContainer.AddView(entry.Card);
                }
            }
        }

        settingsProHiddenCard = OneUiComponents.CreateEmptyPrereqCard(
            this,
            S["SettingsProHiddenTitle"],
            S["SettingsProHiddenSub"],
            S["SettingsSwitchToProCta"],
            () => SetSettingsProMode(true));
        settingsCardsContainer.AddView(settingsProHiddenCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(10)
        });

        settingsSearchEmptyCard = OneUiComponents.CreateEmptyPrereqCard(
            this,
            S["SettingsSearchEmptyTitle"],
            S["SettingsSearchEmptySub"],
            S["SettingsClearSearchCta"],
            ClearSettingsSearch);
        settingsCardsContainer.AddView(settingsSearchEmptyCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(10)
        });

        RefreshSettingsTabButtons();
        RefreshFFmpegCardUi();
        RefreshTruehddCardUi();
        RefreshSettingsUpdateUi();
        RefreshSettingsCategorySubtitles();
        RefreshSettingsHubVisibility();
        return page;
    }

    static int ResolveNeuralTrackCachePresetIndex(int maxMb) {
        int bestIdx = 1;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < NeuralTrackCachePresetsMb.Length; i++) {
            int diff = Math.Abs(maxMb - NeuralTrackCachePresetsMb[i]);
            if (diff < bestDiff) {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    void ApplyNeuralTrackCacheMaxMb(int mb) {
        int clamped = Math.Clamp(mb, 256, 4096);
        store.NeuralTrackCacheMaxMb = clamped;
        neuralTrackCache?.EvictToLimit((long)clamped * 1024L * 1024L);
        SyncNeuralRuntimeDiagnostics();
        RefreshNeuralAudioUi();
        RefreshSettingsCategorySubtitles();
    }

    public bool HasSettingsInternalBackStep() =>
        !string.IsNullOrWhiteSpace(settingsSearchQuery) || activeSettingsCategory >= 0;

    public bool TryHandleSettingsBackStep() {
        if (!string.IsNullOrWhiteSpace(settingsSearchQuery)) {
            ClearSettingsSearch();
            return true;
        }
        if (activeSettingsCategory >= 0) {
            OpenSettingsCategory(-1);
            return true;
        }
        return false;
    }

    public void JumpToSettingsCategory(int catIndex) {
        SwitchTab(4);
        ClearSettingsSearch();
        OpenSettingsCategory(Math.Clamp(catIndex, -1, 5));
    }

    void OpenSettingsCategory(int catIndex) {
        activeSettingsCategory = Math.Clamp(catIndex, -1, 5);
        RefreshSettingsCategorySubtitles();
        RefreshSettingsHubVisibility();
        SyncPredictiveBackRegistration();
    }

    void ClearSettingsSearch() {
        settingsSearchQuery = "";
        if (settingsSearchInput != null && !string.IsNullOrEmpty(settingsSearchInput.Text)) {
            settingsSearchInput.Text = "";
        } else {
            if (settingsSearchClearBtn != null) {
                settingsSearchClearBtn.Visibility = ViewStates.Gone;
            }
            RefreshSettingsHubVisibility();
            SyncPredictiveBackRegistration();
        }
    }

    void SetSettingsProMode(bool proMode) {
        isSettingsProMode = proMode;
        try {
            GetSharedPreferences("cavernize_settings", FileCreationMode.Private)?
                .Edit()?
                .PutBoolean("settings_pro_mode", proMode)?
                .Apply();
        } catch { }
        if (settingsModeToggleBtn != null) {
            settingsModeToggleBtn.Text = isSettingsProMode ? S["SettingsModeProChip"] : S["SettingsModeEssentialChip"];
            settingsModeToggleBtn.Background = isSettingsProMode
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
        }
        if (settingsCategoryStatusChip != null) {
            OneUiComponents.UpdateStatusChip(
                this,
                settingsCategoryStatusChip,
                isSettingsProMode ? S["SettingsModeProChip"] : S["SettingsModeEssentialChip"],
                isSettingsProMode ? OneUiTheme.Primary : OneUiTheme.IconSystemInfo);
        }
        RefreshSettingsHubVisibility();
    }

    static void CollectViewText(View v, System.Text.StringBuilder sb) {
        if (v == null) {
            return;
        }
        if (v is TextView tv && !string.IsNullOrWhiteSpace(tv.Text)) {
            sb.Append(tv.Text).Append(' ');
        } else if (v is ViewGroup vg) {
            int count = vg.ChildCount;
            for (int i = 0; i < count; i++) {
                CollectViewText(vg.GetChildAt(i), sb);
            }
        }
    }

    static bool MatchesSettingsSearch(SettingsCardEntry entry, string query) {
        if (entry?.Card == null || string.IsNullOrWhiteSpace(query)) {
            return true;
        }
        string[] tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) {
            return true;
        }
        System.Text.StringBuilder sb = new(256);
        if (!string.IsNullOrEmpty(entry.ExtraKeywords)) {
            sb.Append(entry.ExtraKeywords).Append(' ');
        }
        CollectViewText(entry.Card, sb);
        string haystack = sb.ToString();
        for (int i = 0; i < tokens.Length; i++) {
            if (haystack.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) < 0) {
                return false;
            }
        }
        return true;
    }

    string GetSettingsCategoryTitle(int catIndex) => catIndex switch {
        0 => S["SettingsCatPlaybackTitle"],
        1 => S["SettingsCatHeadphonesTitle"],
        2 => S["SettingsCatAiTitle"],
        3 => S["SettingsCatProAudioTitle"],
        4 => S["SettingsCatEnginesTitle"],
        5 => S["SettingsCatAboutTitle"],
        _ => S["TabSettings"]
    };

    string BuildSettingsCategoryLiveSubtitle(int catIndex) {
        if (store == null) {
            return "";
        }
        switch (catIndex) {
            case 0: {
                string upmixTag = store.CavernizeUpmix
                    ? S["UpmixModeFull3DShort"]
                    : (store.MatrixUpmixing ? S["UpmixModeSurroundShort"] : S["SignalChainUpmixDirect"]);
                string roomTag = $"{store.RoomSizeX:0.#}×{store.RoomSizeY:0.#}×{store.RoomSizeZ:0.#}m";
                string loudTag = store.Iso226Enabled
                    ? "ISO 226"
                    : (store.LoudnessNormModeIndex > 0 ? "BS.1770" : (store.NormalizerEnabled ? S["NormalizerCheck"] : S["LimiterOnlyCheck"]));
                return $"{upmixTag} · {roomTag} · {loudTag}";
            }
            case 1: {
                string hrirTitle = BinauralProfileService.GetActiveDisplayTitle(store, S);
                string eqTag = store.AutoEqEnabled && !string.IsNullOrWhiteSpace(store.AutoEqProfileName)
                    ? store.AutoEqProfileName
                    : S["StatusOffTag"];
                string extTag = store.IsmRoomEnabled ? "ISM" : (store.BiometricItdEnabled ? "Bio-ITD" : "HRIR");
                return $"{hrirTitle} · AutoEq: {eqTag} · {extTag}";
            }
            case 2: {
                int modelCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
                bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
                string ep = playerEngine?.NeuralDiagnostics.ActiveProviderName ?? (installed ? "XNNPACK" : "CPU");
                string restTag = store.LgsrEnabled
                    ? "LGSR"
                    : (store.SpatialKaraokeEnabled ? "Karaoke" : $"{store.NeuralTrackCacheMaxMb} MB");
                return string.Format(S["SettingsCatAiLiveSubFmt"], ep, modelCount, restTag);
            }
            case 3: {
                string latPart = store.ProAudioEnabled
                    ? S["ProAudioEnableTitle"]
                    : (store.LowLatencyAudio ? S["AudioOutputLowLatTag"] : S["AudioOutputBalancedTag"]);
                string htPart = store.HeadTrackingEnabled ? S["HeadTrackingEnableTitle"] : S["HeadTrackingStatusIdle"];
                string micPart = store.LiveMicEnabled ? S["ProAudioLiveMicTitle"] : S["ProAudioMicOff"];
                return $"{latPart} · {htPart} · {micPart}";
            }
            case 4: {
                bool ffOk = ffmpeg?.Found == true || !string.IsNullOrEmpty(FFmpegInstallerService.ResolveExistingFFmpegPath(this, store));
                bool thdOk = !string.IsNullOrEmpty(TruehddInstallerService.ResolveExistingTruehddPath(this, store));
                bool onnxOk = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
                return string.Format(
                    S["SettingsCatEnginesLiveSubFmt"],
                    ffOk ? "FFmpeg ✓" : "FFmpeg —",
                    thdOk ? "TrueHD ✓" : "TrueHD —",
                    onnxOk ? "ONNX ✓" : "ONNX —");
            }
            case 5: {
                (string verName, _) = AppUpdateService.GetInstalledVersion(this);
                double kb = AppLogger.GetLogSizeBytes() / 1024.0;
                string langTag = string.IsNullOrEmpty(store.LanguageCode) ? S["LangAutoSystem"] : store.LanguageCode;
                return $"v{verName} · {langTag} · {kb:0.1} KB";
            }
            default:
                return "";
        }
    }

    void RefreshSettingsCategorySubtitles() {
        if (uiDestroyed || store == null) {
            return;
        }
        for (int i = 0; i < settingsCatSubtitleViews.Length; i++) {
            TextView tv = settingsCatSubtitleViews[i];
            if (tv != null) {
                tv.Text = BuildSettingsCategoryLiveSubtitle(i);
            }
        }
        if (activeSettingsCategory >= 0 && activeSettingsCategory < 6 && string.IsNullOrWhiteSpace(settingsSearchQuery)) {
            if (settingsCategoryHeaderTitle != null) {
                settingsCategoryHeaderTitle.Text = GetSettingsCategoryTitle(activeSettingsCategory);
            }
            if (settingsCategoryHeaderSub != null) {
                settingsCategoryHeaderSub.Text = BuildSettingsCategoryLiveSubtitle(activeSettingsCategory);
            }
        }
    }

    void RefreshSettingsHubVisibility() {
        if (uiDestroyed) {
            return;
        }
        bool isSearching = !string.IsNullOrWhiteSpace(settingsSearchQuery);
        bool inHubHome = !isSearching && activeSettingsCategory < 0;

        if (settingsHubTilesContainer != null) {
            settingsHubTilesContainer.Visibility = inHubHome ? ViewStates.Visible : ViewStates.Gone;
        }
        if (settingsCategoryHeaderBar != null) {
            settingsCategoryHeaderBar.Visibility = inHubHome ? ViewStates.Gone : ViewStates.Visible;
        }
        if (settingsCardsContainer != null) {
            settingsCardsContainer.Visibility = inHubHome ? ViewStates.Gone : ViewStates.Visible;
        }
        if (inHubHome) {
            return;
        }

        int visibleCount = 0;
        int hiddenProCount = 0;
        for (int i = 0; i < settingsCardsRegistry.Count; i++) {
            SettingsCardEntry entry = settingsCardsRegistry[i];
            if (entry?.Card == null) {
                continue;
            }
            bool show;
            if (isSearching) {
                show = MatchesSettingsSearch(entry, settingsSearchQuery);
                if (show && entry.CategoryIndex == 4 && entry.IsProOnly && advContentContainer != null && !isAdvancedExpanded) {
                    isAdvancedExpanded = true;
                    advContentContainer.Visibility = ViewStates.Visible;
                    if (advChevron != null) {
                        advChevron.IconType = OneUiIconType.ChevronUp;
                    }
                }
            } else {
                bool inCat = entry.CategoryIndex == activeSettingsCategory;
                if (inCat && entry.IsProOnly && !isSettingsProMode) {
                    show = false;
                    hiddenProCount++;
                } else {
                    show = inCat;
                }
            }
            entry.Card.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            if (show) {
                visibleCount++;
            }
        }

        if (settingsNeuralPrereqCard != null) {
            bool showNeuralPrereq = !isSearching &&
                                    activeSettingsCategory == 2 &&
                                    OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count == 0;
            settingsNeuralPrereqCard.Visibility = showNeuralPrereq ? ViewStates.Visible : ViewStates.Gone;
        }
        if (settingsUsbPrereqCard != null) {
            UsbProAudioDeviceSnapshot usbSnap = usbProAudioManager?.CurrentSnapshot;
            bool showUsbPrereq = !isSearching &&
                                 activeSettingsCategory == 3 &&
                                 (usbSnap == null || !usbSnap.IsAttached);
            settingsUsbPrereqCard.Visibility = showUsbPrereq ? ViewStates.Visible : ViewStates.Gone;
        }
        if (settingsProHiddenCard != null) {
            settingsProHiddenCard.Visibility = (!isSearching && hiddenProCount > 0) ? ViewStates.Visible : ViewStates.Gone;
        }
        if (settingsSearchEmptyCard != null) {
            settingsSearchEmptyCard.Visibility = (isSearching && visibleCount == 0) ? ViewStates.Visible : ViewStates.Gone;
        }

        if (isSearching) {
            if (settingsCategoryHeaderTitle != null) {
                settingsCategoryHeaderTitle.Text = string.Format(S["SettingsSearchResultsTitleFmt"], visibleCount);
            }
            if (settingsCategoryHeaderSub != null) {
                settingsCategoryHeaderSub.Text = string.Format(S["SettingsSearchResultsSubFmt"], settingsSearchQuery);
            }
        } else if (activeSettingsCategory >= 0 && activeSettingsCategory < 6) {
            if (settingsCategoryHeaderTitle != null) {
                settingsCategoryHeaderTitle.Text = GetSettingsCategoryTitle(activeSettingsCategory);
            }
            if (settingsCategoryHeaderSub != null) {
                settingsCategoryHeaderSub.Text = BuildSettingsCategoryLiveSubtitle(activeSettingsCategory);
            }
        }
    }

    void RefreshLogSummaryLabel() {
        if (logPathInfoText != null) {
            long bytes = AppLogger.GetLogSizeBytes();
            logPathInfoText.Text = $"{AppLogger.SharedLogPath}\n({bytes / 1024.0:0.1} KB)";
        }
    }

    void SetFFmpegStatus(string msg, double _) {
        if (ffmpegStatusText != null) {
            ffmpegStatusText.Text = msg ?? "";
        }
    }

    void RefreshFFmpegCardUi() {
        if (ffmpegStatusText == null) {
            return;
        }
        string localPath = FFmpegInstallerService.ResolveExistingFFmpegPath(this, store);
        bool hasLocal = ffmpeg.Found || (!string.IsNullOrEmpty(localPath) && File.Exists(localPath));
        bool working = latestFFmpegStatus != null ? latestFFmpegStatus.IsWorking : hasLocal;
        if (hasLocal && working) {
            string ver = !string.IsNullOrEmpty(latestFFmpegStatus?.InstalledVersion)
                ? latestFFmpegStatus.InstalledVersion
                : (!string.IsNullOrEmpty(store.InstalledFFmpegVersion) ? store.InstalledFFmpegVersion : "OK");
            ffmpegStatusText.Text = string.Format(S["FFmpegInstalledVer"], ver);
            ffmpegStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
        } else {
            ffmpegStatusText.Text = S["FFmpegNotInstalled"];
            ffmpegStatusText.SetTextColor(hasLocal ? ColDanger : OneUiTheme.TextSub);
        }
    }

    void SetTruehddStatus(string msg, double _) {
        if (truehddStatusText != null) {
            truehddStatusText.Text = msg ?? "";
        }
    }

    void RefreshTruehddCardUi() {
        if (truehddStatusText == null) {
            return;
        }
        string localPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        bool hasLocal = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        if (hasLocal) {
            string ver = !string.IsNullOrEmpty(latestTruehddStatus?.InstalledVersion)
                ? latestTruehddStatus.InstalledVersion
                : (!string.IsNullOrEmpty(store.InstalledTruehddVersion) ? store.InstalledTruehddVersion : "OK");
            truehddStatusText.Text = string.Format(S["TruehddInstalledVer"], ver);
            truehddStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
        } else {
            truehddStatusText.Text = S["TruehddNotInstalled"];
            truehddStatusText.SetTextColor(OneUiTheme.TextSub);
        }
    }

    void SetSettingsUpdateStatus(string msg, double progress) {
        if (updateSettingsStatusText != null) {
            updateSettingsStatusText.Text = msg ?? "";
        }
        if (updateSettingsProgressBar != null) {
            if (progress < 0) {
                updateSettingsProgressBar.Visibility = ViewStates.Visible;
                updateSettingsProgressBar.Indeterminate = true;
            } else if (progress > 0 && isUpdateDownloading) {
                updateSettingsProgressBar.Visibility = ViewStates.Visible;
                updateSettingsProgressBar.Indeterminate = false;
                updateSettingsProgressBar.Progress = Math.Clamp((int)Math.Round(progress * 1000.0), 0, 1000);
            } else {
                updateSettingsProgressBar.Indeterminate = false;
                updateSettingsProgressBar.Visibility = ViewStates.Gone;
            }
        }
    }

    void RefreshSettingsUpdateUi() {
        if (downloadInstallBtn == null || updateSettingsStatusText == null) {
            return;
        }
        if (latestUpdateInfo?.IsUpdateAvailable == true) {
            double sizeMb = latestUpdateInfo.AssetSizeBytes > 0 ? latestUpdateInfo.AssetSizeBytes / (1024.0 * 1024.0) : 70.0;
            updateSettingsStatusText.Text = string.Format(S["UpdateAvailableBanner"], latestUpdateInfo.RemoteVersion) + $" ({sizeMb:0.1} MB)";
            updateSettingsStatusText.SetTextColor(OneUiTheme.FunctionalOrange);
            downloadInstallBtn.Visibility = ViewStates.Visible;
            downloadInstallBtn.Text = !string.IsNullOrEmpty(downloadedUpdateApkPath) && File.Exists(downloadedUpdateApkPath)
                ? S["InstallReadyBtn"]
                : S["DownloadInstallBtn"];
        } else if (latestUpdateInfo != null) {
            updateSettingsStatusText.Text = S["UpToDateMsg"];
            updateSettingsStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
            downloadInstallBtn.Visibility = ViewStates.Gone;
        }
        if (updateSettingsProgressBar != null && !isUpdateDownloading) {
            updateSettingsProgressBar.Indeterminate = false;
            updateSettingsProgressBar.Visibility = ViewStates.Gone;
        }
    }

    EditText NumBox(string value) {
        EditText et = new(this) {
            Text = value,
            TextSize = 13f,
            InputType = Android.Text.InputTypes.ClassNumber | Android.Text.InputTypes.NumberFlagDecimal
        };
        et.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
        et.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        et.Gravity = GravityFlags.CenterHorizontal;
        et.SetTextColor(ColText);
        return et;
    }

    void ApplyLanguage(string cultureCode) {
        store.LanguageCode = cultureCode;
        LanguageSettings.CultureOverride = string.IsNullOrEmpty(cultureCode) ? null : cultureCode;
        ConversionStrings.Reload();
        ExternalConverterStrings.Reload();
        RenderReportStrings.Reload();
        TrackStrings.Reload();
        ExportFormat.ReloadFormats();
        AndroidStrings.Reload();

        CavernizeTrack savedTrack = selectedTrack;
        AudioFile savedFile = LoadedFile;
        BuildRootUi();
        if (savedFile != null) {
            OpenContent(savedFile);
            if (savedTrack != null) {
                SelectedTrack = savedTrack;
            }
        }
    }

    void ReattachPlayerTrack() {
        if (playerEngine == null) {
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active != null && active.Supported && active.Codec != Codec.TrueHD) {
            bool wasPlaying = playerEngine.IsPlaying;
            double sec = playerEngine.CurrentSeconds;
            playerEngine.Load(active, upmixingSettings, renderingSettings);
            if (sec > 0) {
                playerEngine.Seek(sec);
            }
            trackSourceItems = [.. playerEngine.SourceItems];
            RefreshPlayerSourcesList();
            RefreshExtractorChecklist();
            UpdateRoomCanvas();
            if (wasPlaying) {
                playerEngine.IsPlaying = true;
            }
        }
    }

    static void SyncSlider(OneUiSeekBar bar, int value, int min, int max, string suffix, string overrideValueText = null) {
        if (bar == null) {
            return;
        }
        int clamped = Math.Clamp(value, min, max);
        int targetProg = clamped - min;
        if (bar.Progress != targetProg) {
            bar.Progress = targetProg;
        }
        bar.ValueText = overrideValueText ?? $"{clamped}{suffix}";
        bar.Invalidate();
    }

    void ShowModalGuidedHelp(string title, string body, OneUiIconType icon = OneUiIconType.Info) {
        OneUiModalDialog.Show(
            this,
            title,
            body,
            icon: icon,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["OkBtn"]);
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
        int idx = ResolveUpmixModeIndex();
        if (upmixModeSelector != null && upmixModeSelector.SelectedIndex != idx) {
            upmixModeSelector.SetSelection(idx, notify: false);
        }
        if (atmosAwareUpmixSection != null) {
            atmosAwareUpmixSection.Visibility = idx > 0 ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    void RefreshSettingsTabButtons() {
        RefreshBinauralProfileUi();
        RefreshAutoEqUi();
        RefreshExternalizationUi();
        RefreshPersonalizationUi();
        RefreshLoudnessUi();
        RefreshHeadTrackingUi();
        RefreshProAudioUi();
        RefreshNeuralAudioUi();
        RefreshLgsrUi();
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
        SyncUpmixModeUi();
        if (atmosAwareUpmixSwitch != null && atmosAwareUpmixSwitch.Checked != store.AtmosAwareUpmix) {
            atmosAwareUpmixSwitch.Checked = store.AtmosAwareUpmix;
        }
        SyncSlider(upmixEffectBar, (int)Math.Round(store.UpmixEffect * 100f), 0, 100, "%");
        SyncSlider(upmixSmoothBar, (int)Math.Round(store.UpmixSmoothness * 100f), 0, 100, "%");
        if (normSwitch != null && normSwitch.Checked != store.NormalizerEnabled) {
            normSwitch.Checked = store.NormalizerEnabled;
        }
        if (limiterOnlySwitch != null && limiterOnlySwitch.Checked != store.LimiterOnly) {
            limiterOnlySwitch.Checked = store.LimiterOnly;
        }
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
                    TextView dpBtn = Label(shortLbl, 10.8f, dSel ? Color.White : OneUiTheme.TextSecondary);
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

    void ApplyAndRefreshLoudness() {
        if (store == null || playerEngine == null) {
            return;
        }
        playerEngine.ApplyLoudnessConfig(store.BuildLoudnessConfig());
        RefreshLoudnessUi();
    }

    void RefreshLoudnessUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (iso226EnabledSwitch != null && iso226EnabledSwitch.Checked != store.Iso226Enabled) {
            iso226EnabledSwitch.Checked = store.Iso226Enabled;
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
        if (respectDialnormSwitch != null && respectDialnormSwitch.Checked != store.RespectDolbyDialnorm) {
            respectDialnormSwitch.Checked = store.RespectDolbyDialnorm;
        }
        if (dialogEnhanceSwitch != null && dialogEnhanceSwitch.Checked != store.DialogEnhancementEnabled) {
            dialogEnhanceSwitch.Checked = store.DialogEnhancementEnabled;
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
            int idx = Math.Clamp(store.AutoEqTargetIndex, 0, 4);
            if (autoEqTargetSelector.SelectedIndex != idx) {
                autoEqTargetSelector.SetSelection(idx, notify: false);
            }
        }

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

    void ParseFFmpegExtraArgs(string raw) {
        RenderingSettings.MergeArguments.Clear();
        if (string.IsNullOrWhiteSpace(raw)) {
            return;
        }
        string[] tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++) {
            if (tokens[i].StartsWith('-') && i + 1 < tokens.Length && !tokens[i + 1].StartsWith('-')) {
                RenderingSettings.MergeArguments.Add(new FFmpegArgument(tokens[i], tokens[i + 1]));
                i++;
            } else {
                RenderingSettings.MergeArguments.Add(new FFmpegArgument(tokens[i], null));
            }
        }
    }

    void RequestAllFilesAccess() {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) {
            try {
                Intent intent = new(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                    Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
            } catch {
                Intent intent = new(Android.Provider.Settings.ActionManageAllFilesAccessPermission);
                StartActivity(intent);
            }
        }
    }

    // Queue Helpers
    void AddCurrentToQueue() {
        if (renderQueue.AddCurrent(null)) {
            if (!string.IsNullOrEmpty(activeTrueHDSessionDir)) {
                protectedQueuePaths.Add(Path.GetFullPath(activeTrueHDSessionDir));
            }
            if (!string.IsNullOrEmpty(activeFallbackSessionDir)) {
                protectedQueuePaths.Add(Path.GetFullPath(activeFallbackSessionDir));
            }
            if (!string.IsNullOrEmpty(LoadedFile?.Path)) {
                protectedQueuePaths.Add(Path.GetFullPath(LoadedFile.Path));
            }
            RefreshQueueList();
            Toast.MakeText(this, S["AddQueueBtn"], ToastLength.Short).Show();
        }
    }

    void RemoveLastFromQueue() {
        if (isOperationRunning) {
            ShowError(S["OpRunning"]);
            return;
        }
        if (renderQueue.Jobs.Count > 0) {
            renderQueue.Jobs.RemoveAt(renderQueue.Jobs.Count - 1);
            if (renderQueue.Jobs.Count == 0) {
                protectedQueuePaths.Clear();
            }
            RefreshQueueList();
        }
    }

    void AddLocalPathsToQueue(IReadOnlyList<string> rawPaths) {
        if (rawPaths == null || rawPaths.Count == 0) return;
        StringCollection paths = [];
        foreach (string p in rawPaths) {
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) {
                paths.Add(p);
                protectedQueuePaths.Add(Path.GetFullPath(p));
            }
        }
        if (paths.Count > 0) {
            EnqueueResolvedPaths(paths);
        }
    }

    void AddUrisToQueueAsync(List<Android.Net.Uri> uris) {
        Task.Run(() => {
            StringCollection paths = [];
            foreach (Android.Net.Uri uri in uris) {
                try {
                    string resolved = AndroidFileHelper.ResolveReadablePath(this, uri);
                    if (!string.IsNullOrEmpty(resolved)) {
                        paths.Add(resolved);
                        protectedQueuePaths.Add(Path.GetFullPath(resolved));
                    }
                } catch {
                    // Skip unreadable
                }
            }
            RunOnUiThread(() => EnqueueResolvedPaths(paths));
        });
    }

    void EnqueueResolvedPaths(StringCollection paths) {
        string targetFolder = AndroidFileHelper.GetWorkingOutputDir(this, "Queue");
        List<string> invalids = [];
        suppressAutoTrueHDPrepare = true;
        try {
            renderQueue.AddRange(paths, targetFolder, invalids, ffmpeg);
        } finally {
            suppressAutoTrueHDPrepare = false;
        }
        RefreshQueueList();
        if (invalids.Count > 0) {
            ShowError(string.Format(S["QueueUnsupportedFilesFmt"], string.Join('\n', invalids)));
        }
    }

    void StartQueueProcessing() {
        if (isOperationRunning || isPreparingTrueHD || renderQueue.Jobs.Count == 0) {
            return;
        }
        StopPlayback();
        QueuedJob[] jobs = [.. renderQueue.Jobs];
        RunBackgroundOperation(token => {
            for (int i = 0; i < jobs.Length; i++) {
                token.ThrowIfCancellationRequested();
                QueuedJob job = jobs[i];
                RunOnUiThreadSync(() => {
                    suppressAutoTrueHDPrepare = true;
                    try {
                        job.Prepare(this);
                        CavernizeTrack active = GetActiveTrack();
                        if (active != null && active.Codec != Codec.TrueHD) {
                            AttachToListener(active);
                        }
                    } finally {
                        suppressAutoTrueHDPrepare = false;
                    }
                });
                job.Run();
                RunOnUiThreadSync(() => {
                    renderQueue.Jobs.Remove(job);
                    RefreshQueueList();
                });
            }
            RunOnUiThreadSync(() => {
                protectedQueuePaths.Clear();
                Reset();
                TrueHDDecoderService.CleanupOrphanedCache(this);
            });
        }, null);
    }

    void RefreshQueueList() {
        if (queueListContainer == null) {
            return;
        }
        queueListContainer.RemoveAllViews();
        if (renderQueue.Jobs.Count == 0) {
            queueListContainer.AddView(Label(S["QueueEmptyZeroJobs"], 12f, ColMuted));
            return;
        }
        foreach (QueuedJob job in renderQueue.Jobs) {
            queueListContainer.AddView(Label($"• {job}", 12f, ColText));
        }
    }

    // Slot 2B / Slot 7A-7B: Neural Audio Runtime (ONNX), Dynamic Orchestrator, Lazy/Custom Models & Track Cache Helpers
    void SyncNeuralRuntimeDiagnostics() {
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
        bool qnn = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(this) &&
                   OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        int modelCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
        long totalBytes = OnnxRuntimeInstallerService.GetModelsAndRuntimeSizeBytes(this);
        Cavernize.Logic.Spatial.NeuralTrackCacheStats cacheStats = neuralTrackCache?.GetStats() ?? default;
        string planSummary = neuralOrchestrator != null
            ? neuralOrchestrator.BuildPlan(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, store.BuildNeuralAudioConfig()).Summary
            : string.Empty;
        string epName = !installed
            ? "Not Installed"
            : store.NeuralProviderIndex switch {
                1 => qnn ? "QNN HTP" : "XNNPACK",
                2 => "XNNPACK",
                3 => "CPU",
                _ => qnn ? "QNN HTP" : "XNNPACK"
            };
        playerEngine?.NeuralStage.UpdateRuntimeDiagnostics(
            installed,
            store.InstalledOnnxRuntimeVersion,
            epName,
            qnn,
            modelCount,
            totalBytes,
            trackCacheEntryCount: cacheStats.EntryCount,
            trackCacheSizeBytes: cacheStats.TotalBytes,
            orchestrationSummary: planSummary);
        if (playerEngine != null) {
            playerEngine.SceneHub.NeuralDiagnostics = playerEngine.NeuralStage.BuildDiagnosticsSnapshot();
        }
    }

    void ApplyAndRefreshNeuralAudio() {
        playerEngine?.ApplyNeuralConfig(store.BuildNeuralAudioConfig());
        SyncNeuralRuntimeDiagnostics();
        RefreshNeuralAudioUi();
    }

    void RefreshNeuralAudioUi() {
        if (uiDestroyed) {
            return;
        }
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
        int modelCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
        double cacheMb = OnnxRuntimeInstallerService.GetModelsAndRuntimeSizeBytes(this) / (1024.0 * 1024.0);
        string epName = playerEngine?.NeuralDiagnostics.ActiveProviderName ?? (installed ? "XNNPACK" : "CPU");

        if (neuralModelsBtn != null) {
            neuralModelsBtn.Text = string.Format(S["NeuralModelsBtn"], modelCount);
        }
        if (neuralRuntimeStatusText != null) {
            if (!installed) {
                neuralRuntimeStatusText.Text = S["NeuralRuntimeNotInstalled"];
                neuralRuntimeStatusText.SetTextColor(OneUiTheme.TextSub);
            } else {
                string ver = string.IsNullOrEmpty(store.InstalledOnnxRuntimeVersion) ? "local" : "v" + store.InstalledOnnxRuntimeVersion;
                neuralRuntimeStatusText.Text = string.Format(S["NeuralRuntimeInstalled"], ver, epName, modelCount, cacheMb);
                neuralRuntimeStatusText.SetTextColor(OneUiTheme.IconSystemInfo);
            }
        }
        if (neuralPlanSummaryText != null) {
            string summary = playerEngine?.NeuralDiagnostics.OrchestrationSummary ?? "";
            Cavernize.Logic.Spatial.NeuralTrackCacheStats cacheStats = neuralTrackCache?.GetStats() ?? default;
            double trackCacheMb = cacheStats.TotalBytes / (1024.0 * 1024.0);
            string planLine = !string.IsNullOrEmpty(summary)
                ? string.Format(S["NeuralPlanPreviewFmt"], summary)
                : "";
            string cacheSuffix = cacheStats.EntryCount > 0 ? $" · {cacheStats.EntryCount} ({trackCacheMb:0.0} MB)" : "";
            neuralPlanSummaryText.Text = planLine + cacheSuffix;
            neuralPlanSummaryText.Visibility = string.IsNullOrEmpty(neuralPlanSummaryText.Text) ? ViewStates.Gone : ViewStates.Visible;
        }
        if (neuralTrackCacheCapSelector != null && store != null) {
            int capIdx = ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb);
            if (neuralTrackCacheCapSelector.SelectedIndex != capIdx) {
                neuralTrackCacheCapSelector.SetSelection(capIdx, notify: false);
            }
        }
        if (store != null) {
            if (neuralProviderSelector != null) {
                int pIdx = Math.Clamp(store.NeuralProviderIndex, 0, 3);
                if (neuralProviderSelector.SelectedIndex != pIdx) {
                    neuralProviderSelector.SetSelection(pIdx, notify: false);
                }
            }
            if (neuralOrchestrationSelector != null) {
                int oIdx = Math.Clamp(store.NeuralOrchestrationModeIndex, 0, 1);
                if (neuralOrchestrationSelector.SelectedIndex != oIdx) {
                    neuralOrchestrationSelector.SetSelection(oIdx, notify: false);
                }
            }
            SyncSlider(neuralOverlapBar, store.NeuralOverlapFactor, 2, 8, "x");
            if (neuralTtaSwitch != null && neuralTtaSwitch.Checked != store.NeuralTtaEnabled) {
                neuralTtaSwitch.Checked = store.NeuralTtaEnabled;
            }
            if (neuralEnsembleSelector != null) {
                int eIdx = Math.Clamp(store.NeuralEnsembleModeIndex, 0, 2);
                if (neuralEnsembleSelector.SelectedIndex != eIdx) {
                    neuralEnsembleSelector.SetSelection(eIdx, notify: false);
                }
            }
            if (neuralPhaseFixSwitch != null && neuralPhaseFixSwitch.Checked != store.NeuralPhaseFixEnabled) {
                neuralPhaseFixSwitch.Checked = store.NeuralPhaseFixEnabled;
            }
            if (neuralConservationSwitch != null && neuralConservationSwitch.Checked != store.NeuralEnergyConservation) {
                neuralConservationSwitch.Checked = store.NeuralEnergyConservation;
            }
            if (spatialKaraokeSwitch != null && spatialKaraokeSwitch.Checked != store.SpatialKaraokeEnabled) {
                spatialKaraokeSwitch.Checked = store.SpatialKaraokeEnabled;
            }
            SyncSlider(karaokeVocalLevelBar, (int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100, "%");
            if (karaokePreserveBackingSwitch != null && karaokePreserveBackingSwitch.Checked != store.KaraokePreserveBackingVocals) {
                karaokePreserveBackingSwitch.Checked = store.KaraokePreserveBackingVocals;
            }
            if (karaokeLosslessTwinSwitch != null && karaokeLosslessTwinSwitch.Checked != store.UseLosslessTwinForKaraoke) {
                karaokeLosslessTwinSwitch.Checked = store.UseLosslessTwinForKaraoke;
            }
            if (neuralUpmixModeSelector != null) {
                int uIdx = Math.Clamp(store.NeuralSpatialUpmixModeIndex, 0, 2);
                if (neuralUpmixModeSelector.SelectedIndex != uIdx) {
                    neuralUpmixModeSelector.SetSelection(uIdx, notify: false);
                }
            }
        }
        RefreshSettingsCategorySubtitles();
        if (activeSettingsCategory == 2) {
            RefreshSettingsHubVisibility();
        }
    }

    async Task CheckOnnxRuntimeUpdateAsync(bool silent) {
        try {
            bool preferQnn = store.NeuralProviderIndex == (int)Cavernize.Logic.Spatial.NeuralExecutionProvider.QnnHtp ||
                             store.NeuralProviderIndex == (int)Cavernize.Logic.Spatial.NeuralExecutionProvider.Auto;
            OnnxRuntimeStatusResult status = await OnnxRuntimeInstallerService.CheckStatusAsync(this, store, preferQnn);
            latestOnnxStatus = status;
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                SyncNeuralRuntimeDiagnostics();
                RefreshNeuralAudioUi();
                if (!silent) {
                    if (status.IsInstalled) {
                        ShowToast(string.Format(S["NeuralRuntimeUpToDate"], string.IsNullOrEmpty(status.InstalledVersion) ? "local" : "v" + status.InstalledVersion));
                    } else {
                        ShowToast(S["NeuralRuntimeNotInstalled"]);
                    }
                }
            });
        } catch (Exception ex) {
            AppLogger.Warn($"ONNX Runtime check failed: {ex.Message}");
            if (!silent) {
                RunOnUiThread(() => ShowError(ex.Message));
            }
        }
    }

    void ShowNeuralModelsModal() {
        Android.App.Dialog dialog = null;
        bool modalClosed = false;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView summaryTv = Label("", 12f, OneUiTheme.IconSystemInfo);
        summaryTv.SetPadding(0, 0, 0, Dp(4));
        content.AddView(summaryTv);

        TextView trackCacheTv = Label("", 11.5f, OneUiTheme.TextSub);
        trackCacheTv.SetPadding(0, 0, 0, Dp(4));
        content.AddView(trackCacheTv);

        OneUiInlineSelector modalCacheCapSelector = new(this, S["NeuralTrackCacheCapTitle"]);
        string[] modalCacheCapLabels = ["256 MB", "512 MB", "1024 MB", "2048 MB", "4096 MB"];
        modalCacheCapSelector.SetItems(modalCacheCapLabels, ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb));
        modalCacheCapSelector.SelectionChanged += pos => {
            int mb = NeuralTrackCachePresetsMb[Math.Clamp(pos, 0, NeuralTrackCachePresetsMb.Length - 1)];
            ApplyNeuralTrackCacheMaxMb(mb);
            Cavernize.Logic.Spatial.NeuralTrackCacheStats updatedStats = neuralTrackCache?.GetStats() ?? default;
            double updatedMb = updatedStats.TotalBytes / (1024.0 * 1024.0);
            trackCacheTv.Text = string.Format(S["NeuralTrackCacheSummary"], updatedStats.EntryCount, updatedMb, store.NeuralTrackCacheMaxMb);
        };
        content.AddView(modalCacheCapSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView planTv = null;
        if (neuralOrchestrator != null) {
            planTv = Label("", 11.5f, OneUiTheme.Primary);
            planTv.SetSingleLine(true);
            planTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            planTv.SetPadding(0, 0, 0, Dp(8));
            content.AddView(planTv);
        }

        Button importStandaloneBtn = PrimaryButton(S["NeuralImportStandaloneBtn"], () => {
            dialog?.Dismiss();
            ShowImportCustomOnnxDialog();
        });
        content.AddView(importStandaloneBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        ProgressBar modalProg = new(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0,
            Visibility = ViewStates.Gone
        };
        OneUiTheme.StyleProgressBar(this, modalProg);
        content.AddView(modalProg, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(12)) { BottomMargin = Dp(8) });

        TextView modalStatusTv = Label("", 11.5f, OneUiTheme.TextSub);
        modalStatusTv.Visibility = ViewStates.Gone;
        content.AddView(modalStatusTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        CancellationTokenSource activeDownloadCts = null;
        bool downloadInFlight = false;
        Button cancelDlBtn = null;
        cancelDlBtn = PillButton(S["NeuralModelCancelDownloadBtn"], () => {
            try { activeDownloadCts?.Cancel(); } catch { }
            if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
        });
        cancelDlBtn.Visibility = ViewStates.Gone;
        content.AddView(cancelDlBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        LinearLayout modelsContainer = new(this) { Orientation = Orientation.Vertical };
        content.AddView(modelsContainer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        void RefreshModalInPlace(bool preserveScroll = true) {
            if (uiDestroyed || modalClosed || store == null) {
                return;
            }
            ScrollView hostScroll = content.Parent?.Parent as ScrollView;
            int savedScrollY = preserveScroll && hostScroll != null ? hostScroll.ScrollY : 0;

            Cavernize.Logic.Spatial.NeuralAudioConfig currentCfg = store.BuildNeuralAudioConfig();
            int installedCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
            double cacheMb = OnnxRuntimeInstallerService.GetModelsAndRuntimeSizeBytes(this) / (1024.0 * 1024.0);
            summaryTv.Text = string.Format(S["NeuralModelsCacheSummary"], cacheMb, installedCount);

            Cavernize.Logic.Spatial.NeuralTrackCacheStats trackCacheStats = neuralTrackCache?.GetStats() ?? default;
            double trackCacheMb = trackCacheStats.TotalBytes / (1024.0 * 1024.0);
            trackCacheTv.Text = string.Format(S["NeuralTrackCacheSummary"], trackCacheStats.EntryCount, trackCacheMb, store.NeuralTrackCacheMaxMb);

            int capIdx = ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb);
            if (modalCacheCapSelector.SelectedIndex != capIdx) {
                modalCacheCapSelector.SetSelection(capIdx, notify: false);
            }

            if (neuralOrchestrator != null && planTv != null) {
                Cavernize.Logic.Spatial.NeuralExecutionPlan plan =
                    neuralOrchestrator.BuildPlan(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, currentCfg);
                planTv.Text = string.Format(S["NeuralPlanPreviewFmt"], plan.Summary);
            }

            modelsContainer.RemoveAllViews();
            List<Cavernize.Logic.Spatial.NeuralModelCatalogEntry> allModels = OnnxRuntimeInstallerService.GetAllRegisteredModels(this);
            foreach (Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry in allModels) {
                Cavernize.Logic.Spatial.NeuralModelCatalogEntry localEntry = entry;
                bool isInst = OnnxRuntimeInstallerService.IsModelInstalled(this, localEntry.Id);
                IReadOnlyList<string> taskSlots = currentCfg.GetModelsForTask(localEntry.Task);
                bool inTaskSlot = taskSlots.Any(id => string.Equals(id, localEntry.Id, StringComparison.OrdinalIgnoreCase));
                bool isPrimary = string.Equals(localEntry.Id, store.ActiveStemModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveKaraokeModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveDereverbModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveDebleedModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveRestorerModelId, StringComparison.OrdinalIgnoreCase);
                bool isActive = isPrimary || inTaskSlot;

                LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
                tile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
                tile.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

                string originTag = localEntry.IsCustomImported ? S["NeuralModelCustomTag"] : localEntry.License;
                TextView titleTv = Label($"{localEntry.DisplayName} ({localEntry.ApproxSizeMb:0.0} MB · {originTag})", 12.5f, ColText);
                titleTv.SetTypeface(null, TypefaceStyle.Bold);
                titleTv.SetSingleLine(true);
                titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                tile.AddView(titleTv);

                string stemsTag = localEntry.OutputStemNames != null && localEntry.OutputStemNames.Length > 0
                    ? string.Join(", ", localEntry.OutputStemNames)
                    : localEntry.Task.ToString();
                string statusHint = isInst
                    ? S["NeuralModelInstalledTag"]
                    : (localEntry.RequiresManualOnnxImport ? S["NeuralModelManualImportHint"] : localEntry.SourceRepository);
                TextView subTv = Label($"{localEntry.Task} · {stemsTag} [{statusHint}]", 11.5f, isInst ? OneUiTheme.IconSystemInfo : OneUiTheme.TextSub);
                subTv.SetPadding(0, Dp(2), 0, Dp(8));
                tile.AddView(subTv);

                List<Button> actionButtons = [];
                if (!isInst && !string.IsNullOrWhiteSpace(localEntry.DownloadUrl)) {
                    actionButtons.Add(PrimaryButton(S["NeuralModelDownloadBtn"], () => {
                        if (downloadInFlight) {
                            ShowToast(S["NeuralModelDownloadInProgressToast"]);
                            return;
                        }
                        downloadInFlight = true;
                        activeDownloadCts?.Dispose();
                        activeDownloadCts = new CancellationTokenSource();
                        CancellationToken dlToken = activeDownloadCts.Token;
                        modalProg.Progress = 0;
                        modalProg.Visibility = ViewStates.Visible;
                        modalStatusTv.Text = $"{localEntry.DisplayName}...";
                        modalStatusTv.Visibility = ViewStates.Visible;
                        if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Visible;
                        Task.Run(async () => {
                            try {
                                await OnnxRuntimeInstallerService.DownloadModelAsync(this, localEntry, (frac, msg) => RunOnUiThread(() => {
                                    if (uiDestroyed || modalClosed) return;
                                    modalProg.Progress = Math.Clamp((int)Math.Round(frac * 1000.0), 0, 1000);
                                    modalStatusTv.Text = msg;
                                }), dlToken);
                                RunOnUiThread(() => {
                                    downloadInFlight = false;
                                    if (uiDestroyed) return;
                                    SyncNeuralRuntimeDiagnostics();
                                    RefreshNeuralAudioUi();
                                    if (!modalClosed) {
                                        modalProg.Visibility = ViewStates.Gone;
                                        modalStatusTv.Visibility = ViewStates.Gone;
                                        if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
                                        RefreshModalInPlace();
                                    }
                                });
                            } catch (System.OperationCanceledException) {
                                RunOnUiThread(() => {
                                    downloadInFlight = false;
                                    if (uiDestroyed || modalClosed) return;
                                    modalProg.Visibility = ViewStates.Gone;
                                    modalStatusTv.Visibility = ViewStates.Gone;
                                    if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
                                });
                            } catch (Exception ex) {
                                RunOnUiThread(() => {
                                    downloadInFlight = false;
                                    if (uiDestroyed) return;
                                    modalProg.Visibility = ViewStates.Gone;
                                    if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
                                    modalStatusTv.Text = ex.Message;
                                    if (!modalClosed) {
                                        ShowModelDownloadErrorDialog(localEntry, ex.Message, onOpenImport: () => {
                                            pendingStandaloneCustomOnnxImport = false;
                                            pendingOnnxImportModelId = localEntry.Id;
                                            pendingCustomOnnxTask = localEntry.Task;
                                            dialog?.Dismiss();
                                            OpenFileWithExplorerForExtensions(S["NeuralModelImportBtn"], [".onnx"], ImportCustomOnnxModelFromPath, ReqLoadOnnxModel);
                                        });
                                    }
                                });
                            }
                        });
                    }));
                } else if (isInst) {
                    actionButtons.Add(PillButton(isActive ? S["NeuralModelActiveTag"] : S["NeuralModelSelectBtn"], () => {
                        AssignActiveModelSlot(localEntry, toggleEnsemble: false);
                        ApplyAndRefreshNeuralAudio();
                        RefreshModalInPlace();
                    }, active: isActive));

                    if (store.NeuralOrchestrationModeIndex == (int)Cavernize.Logic.Spatial.NeuralOrchestrationMode.Manual) {
                        actionButtons.Add(PillButton(inTaskSlot ? S["NeuralModelEnsembleInTag"] : S["NeuralModelEnsembleAddBtn"], () => {
                            AssignActiveModelSlot(localEntry, toggleEnsemble: true);
                            ApplyAndRefreshNeuralAudio();
                            RefreshModalInPlace();
                        }, active: inTaskSlot));
                    }

                    actionButtons.Add(PillButton(S["NeuralModelDeleteBtn"], () => {
                        OnnxRuntimeInstallerService.DeleteModel(this, localEntry.Id);
                        SyncNeuralRuntimeDiagnostics();
                        RefreshNeuralAudioUi();
                        RefreshModalInPlace();
                    }));
                }

                if (!localEntry.IsCustomImported) {
                    actionButtons.Add(PillButton(S["NeuralModelImportBtn"], () => {
                        pendingStandaloneCustomOnnxImport = false;
                        pendingOnnxImportModelId = localEntry.Id;
                        pendingCustomOnnxTask = localEntry.Task;
                        dialog?.Dismiss();
                        OpenFileWithExplorerForExtensions(S["NeuralModelImportBtn"], [".onnx"], ImportCustomOnnxModelFromPath, ReqLoadOnnxModel);
                    }));
                    if (!string.IsNullOrWhiteSpace(localEntry.DownloadUrl)) {
                        actionButtons.Add(PillButton(S["NeuralModelOpenLinkBtn"], () => OpenModelUrlInBrowser(localEntry.DownloadUrl)));
                    }
                }

                // 2-row flex layout so 3-4 action pills never clip horizontally
                for (int idx = 0; idx < actionButtons.Count; idx += 2) {
                    LinearLayout row = HRow();
                    row.BaselineAligned = false;
                    row.SetGravity(GravityFlags.CenterVertical);
                    Button first = actionButtons[idx];
                    first.TextSize = 11.6f;
                    first.SetSingleLine(true);
                    first.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    first.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));

                    bool hasSecond = idx + 1 < actionButtons.Count;
                    row.AddView(first, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                        RightMargin = hasSecond ? Dp(6) : 0
                    });
                    if (hasSecond) {
                        Button second = actionButtons[idx + 1];
                        second.TextSize = 11.6f;
                        second.SetSingleLine(true);
                        second.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                        second.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.AddView(second, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                    }
                    tile.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        TopMargin = idx > 0 ? Dp(6) : 0
                    });
                }

                modelsContainer.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });
            }

            if (preserveScroll && hostScroll != null) {
                hostScroll.Post(() => {
                    if (!uiDestroyed && !modalClosed) {
                        hostScroll.ScrollTo(0, savedScrollY);
                    }
                });
            }
        }

        RefreshModalInPlace(preserveScroll: false);

        Button clearTracksBtn = PillButton(S["NeuralClearTrackCacheBtn"], () => {
            neuralTrackCache?.Clear();
            SyncNeuralRuntimeDiagnostics();
            RefreshNeuralAudioUi();
            ShowToast(S["NeuralTrackCacheClearedToast"]);
            RefreshModalInPlace();
        });
        content.AddView(clearTracksBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(6)
        });

        LinearLayout footerRow = HRow();
        Button licBtn = PillButton(S["NeuralLicensesBtn"], () => {
            ShowMessageDialog(S["NeuralLicensesBtn"], Cavernize.Logic.Spatial.NeuralModelCatalog.BuildAttributionNotice(), OneUiIconType.Info);
        });
        Button clearBtn = PillButton(S["NeuralClearCacheBtn"], () => {
            OnnxRuntimeInstallerService.ClearAllModelsAndCache(this);
            neuralTrackCache?.Clear();
            SyncNeuralRuntimeDiagnostics();
            RefreshNeuralAudioUi();
            ShowToast(S["NeuralCacheClearedToast"]);
            dialog?.Dismiss();
        });
        footerRow.AddView(licBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        footerRow.AddView(clearBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(footerRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });

        dialog = OneUiModalDialog.Show(
            this,
            S["NeuralModelsModalTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["CloseBtn"],
            onHelp: () => ShowModalGuidedHelp(S["NeuralModelsHelpTitle"], S["NeuralModelsHelpBody"], OneUiIconType.Sparkles),
            helpContentDesc: S["NeuralModelsHelpTitle"]);
        if (dialog != null) {
            dialog.DismissEvent += (_, _) => {
                modalClosed = true;
                try { activeDownloadCts?.Cancel(); } catch { }
                activeDownloadCts?.Dispose();
                activeDownloadCts = null;
            };
        }
    }

    void OpenModelUrlInBrowser(string url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return;
        }
        try {
            Intent browser = new(Intent.ActionView, Android.Net.Uri.Parse(url.Trim()));
            StartActivity(browser);
        } catch (Exception ex) {
            ShowError(ex.Message);
        }
    }

    void CopyModelUrlToClipboard(string url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return;
        }
        if (GetSystemService(ClipboardService) is ClipboardManager cm) {
            cm.PrimaryClip = ClipData.NewPlainText("ONNX Model URL", url.Trim());
            ShowToast(S["NeuralModelLinkCopiedToast"]);
        }
    }

    void ShowModelDownloadErrorDialog(Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry, string errorDetail, Action onOpenImport) {
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        TextView detailTv = Label(errorDetail ?? "", 12.5f, OneUiTheme.TextSecondary);
        detailTv.SetPadding(0, 0, 0, Dp(10));
        body.AddView(detailTv);

        string url = entry?.DownloadUrl ?? "";
        if (!string.IsNullOrWhiteSpace(url)) {
            TextView urlBox = new(this) {
                Text = url,
                TextSize = 11.5f
            };
            urlBox.SetTextColor(OneUiTheme.PrimaryHigh);
            urlBox.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            urlBox.SetTextIsSelectable(true);
            urlBox.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
            urlBox.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            body.AddView(urlBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(10)
            });

            LinearLayout linkRow = HRow();
            linkRow.BaselineAligned = false;
            linkRow.SetGravity(GravityFlags.CenterVertical);

            Button openBtn = PrimaryButton(S["NeuralModelOpenLinkBtn"], () => OpenModelUrlInBrowser(url));
            openBtn.TextSize = 12f;
            openBtn.SetSingleLine(true);
            openBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

            Button copyBtn = PillButton(S["NeuralModelCopyLinkBtn"], () => CopyModelUrlToClipboard(url));
            copyBtn.TextSize = 12f;
            copyBtn.SetSingleLine(true);
            copyBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

            linkRow.AddView(openBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
            linkRow.AddView(copyBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            body.AddView(linkRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        OneUiModalDialog.Show(
            this,
            S["NeuralModelDownloadFailedTitle"],
            customContent: body,
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.FunctionalAmber,
            positiveText: S["NeuralModelImportBtn"],
            onPositive: onOpenImport,
            negativeText: S["CloseBtn"]);
    }

    void ShowImportCustomOnnxDialog() {
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        Cavernize.Logic.Spatial.NeuralModelTask[] taskOptions = [
            Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation4,
            Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation6,
            Cavernize.Logic.Spatial.NeuralModelTask.VocalSeparation,
            Cavernize.Logic.Spatial.NeuralModelTask.KaraokeLeadBacking,
            Cavernize.Logic.Spatial.NeuralModelTask.Dereverb,
            Cavernize.Logic.Spatial.NeuralModelTask.DebleedDenoise,
            Cavernize.Logic.Spatial.NeuralModelTask.CodecRestoreApollo,
            Cavernize.Logic.Spatial.NeuralModelTask.BandwidthExtension
        ];
        string[] taskLabels = [
            S["NeuralTaskStem4"],
            S["NeuralTaskStem6"],
            S["NeuralTaskVocal"],
            S["NeuralTaskKaraoke"],
            S["NeuralTaskDereverb"],
            S["NeuralTaskDebleed"],
            S["NeuralTaskRestore"],
            S["NeuralTaskBwe"]
        ];

        int selectedIdx = Array.IndexOf(taskOptions, pendingCustomOnnxTask);
        if (selectedIdx < 0) selectedIdx = 0;

        OneUiInlineSelector taskSelector = new(this, S["NeuralImportTaskTitle"]);
        taskSelector.SetItems(taskLabels, selectedIdx);
        taskSelector.SelectionChanged += pos => {
            if (pos >= 0 && pos < taskOptions.Length) {
                pendingCustomOnnxTask = taskOptions[pos];
            }
        };
        body.AddView(taskSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        OneUiModalDialog.Show(
            this,
            S["NeuralImportStandaloneBtn"],
            customContent: body,
            icon: OneUiIconType.Folder,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["NeuralModelImportBtn"],
            onPositive: () => {
                pendingStandaloneCustomOnnxImport = true;
                pendingOnnxImportModelId = "";
                OpenFileWithExplorerForExtensions(S["NeuralImportStandaloneBtn"], [".onnx"], ImportCustomOnnxModelFromPath, ReqLoadOnnxModel);
            },
            negativeText: S["CancelBtn"]);
    }

    void AssignActiveModelSlot(Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry, bool toggleEnsemble = false) {
        if (entry == null) return;
        if (toggleEnsemble) {
            store.ToggleNeuralTaskModelId(entry.Task, entry.Id);
        } else {
            store.SetNeuralTaskModelIds(entry.Task, [entry.Id]);
        }

        switch (entry.Task) {
            case Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation4:
            case Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation6:
            case Cavernize.Logic.Spatial.NeuralModelTask.VocalSeparation:
                store.ActiveStemModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.KaraokeLeadBacking:
                store.ActiveKaraokeModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.Dereverb:
                store.ActiveDereverbModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.DebleedDenoise:
                store.ActiveDebleedModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.CodecRestoreApollo:
            case Cavernize.Logic.Spatial.NeuralModelTask.BandwidthExtension:
                store.ActiveRestorerModelId = entry.Id;
                break;
        }
    }

    void ImportCustomOnnxModelFromPath(string path) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        try {
            Cavernize.Logic.Spatial.NeuralModelCatalogEntry imported;
            if (pendingStandaloneCustomOnnxImport) {
                imported = OnnxRuntimeInstallerService.ImportCustomOnnxModel(
                    this,
                    path,
                    targetModelId: "",
                    customTask: pendingCustomOnnxTask);
            } else {
                string slotId = string.IsNullOrEmpty(pendingOnnxImportModelId) ? store.ActiveStemModelId : pendingOnnxImportModelId;
                imported = OnnxRuntimeInstallerService.ImportCustomOnnxModel(
                    this,
                    path,
                    slotId,
                    customTask: pendingCustomOnnxTask);
            }
            pendingStandaloneCustomOnnxImport = false;
            AssignActiveModelSlot(imported, toggleEnsemble: false);
            ApplyAndRefreshNeuralAudio();
            ShowToast(string.Format(S["NeuralModelImportedToast"], imported?.DisplayName ?? Path.GetFileNameWithoutExtension(path)));
        } catch (Exception ex) {
            pendingStandaloneCustomOnnxImport = false;
            ShowError(ex.Message);
        }
    }

    // Slot 2C: Lossless-Guided Atmos Restoration (LGSR, W09) Helpers
    void ApplyAndRefreshLgsr(bool rescanTwin) {
        playerEngine?.ApplyLgsrConfig(store.BuildLgsrConfig());
        if (rescanTwin) {
            CavernizeTrack active = GetActiveTrack();
            if (active != null) {
                StartLosslessTwinDiscoveryAsync(active, forceRescan: true);
                return;
            }
        }
        RefreshLgsrUi();
    }

    void RefreshLgsrUi() {
        if (uiDestroyed || lgsrStatusText == null) {
            return;
        }
        if (store != null) {
            if (lgsrEnabledSwitch != null && lgsrEnabledSwitch.Checked != store.LgsrEnabled) {
                lgsrEnabledSwitch.Checked = store.LgsrEnabled;
            }
            if (lgsrAutoDiscoverSwitch != null && lgsrAutoDiscoverSwitch.Checked != store.LgsrAutoDiscoverTwin) {
                lgsrAutoDiscoverSwitch.Checked = store.LgsrAutoDiscoverTwin;
            }
            SyncSlider(lgsrFusionBar, (int)Math.Round(store.LgsrFusionStrength * 100f), 0, 100, "%");
            if (lgsrEnvelopeLockSwitch != null && lgsrEnvelopeLockSwitch.Checked != store.LgsrDynamicEnvelopeLock) {
                lgsrEnvelopeLockSwitch.Checked = store.LgsrDynamicEnvelopeLock;
            }
            if (lgsrClippingGuardSwitch != null && lgsrClippingGuardSwitch.Checked != store.LgsrClippingGuard) {
                lgsrClippingGuardSwitch.Checked = store.LgsrClippingGuard;
            }
            if (lgsrBitstreamGuideSwitch != null && lgsrBitstreamGuideSwitch.Checked != store.LgsrBitstreamGuide) {
                lgsrBitstreamGuideSwitch.Checked = store.LgsrBitstreamGuide;
            }
            if (lgsrRejectFakeSwitch != null && lgsrRejectFakeSwitch.Checked != store.LgsrRejectFakeLossless) {
                lgsrRejectFakeSwitch.Checked = store.LgsrRejectFakeLossless;
            }
            if (lgsrTier3CleanupSwitch != null && lgsrTier3CleanupSwitch.Checked != store.LgsrTier3ResidualCleanup) {
                lgsrTier3CleanupSwitch.Checked = store.LgsrTier3ResidualCleanup;
            }
        }
        Cavernize.Logic.Spatial.LgsrDiagnostics diag = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        switch (diag.State) {
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced: {
                string fmt = string.IsNullOrEmpty(diag.TwinFormatSummary) ? "FLAC" : diag.TwinFormatSummary;
                string twin = string.IsNullOrEmpty(diag.TwinFileName) ? "Lossless Twin" : diag.TwinFileName;
                lgsrStatusText.Text = string.Format(
                    S["LgsrStatusSynced"],
                    fmt,
                    twin,
                    diag.SyncOffsetMs,
                    diag.DriftPpm,
                    diag.AverageCoherence * 100f,
                    diag.CouplingStartHz / 1000f,
                    diag.SpxStartHz / 1000f);
                lgsrStatusText.SetTextColor(OneUiTheme.IconSystemInfo);
                break;
            }
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.Searching:
                lgsrStatusText.Text = S["LgsrStatusSearching"];
                lgsrStatusText.SetTextColor(OneUiTheme.Primary);
                break;
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless:
                lgsrStatusText.Text = S["LgsrStatusBypassedLossless"];
                lgsrStatusText.SetTextColor(OneUiTheme.TextSub);
                break;
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedFakeLossless:
                lgsrStatusText.Text = S["LgsrStatusBypassedFake"];
                lgsrStatusText.SetTextColor(OneUiTheme.PeachText);
                break;
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedLowConfidence:
                lgsrStatusText.Text = S["LgsrStatusBypassedLowConf"];
                lgsrStatusText.SetTextColor(OneUiTheme.TextSub);
                break;
            default:
                lgsrStatusText.Text = S["LgsrStatusNone"];
                lgsrStatusText.SetTextColor(OneUiTheme.TextSub);
                break;
        }
        if (lgsrAbBypassBtn != null) {
            bool byp = playerEngine?.LgsrBypassed ?? false;
            bool synced = diag.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
            lgsrAbBypassBtn.Text = $"{S["LgsrAbToggleBtn"]}: {(byp ? S["XRayBypassActive"] : S["XRayActiveLive"])}";
            lgsrAbBypassBtn.Background = synced
                ? (byp
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 68), 14f, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 165), 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 68), 14f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 165), 1f))
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
        }
        UpdateSpatialBadgePillVisuals();
        RefreshStudioW12ContextUi();
    }

    void ShowLgsrLicensesModal() {
        OneUiModalDialog.Show(
            this,
            S["LgsrLicensesTitle"],
            Cavernize.Logic.Spatial.LgsrDiagnostics.BuildAttributionNotice(),
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"]);
    }

    // Slot 1 / Slot 4: W10 Pro-Audio Full-Duplex USB Engine & Focusrite Scarlett 2i2 Helpers
    void ApplyAndRefreshProAudio() {
        if (store == null) {
            return;
        }
        Cavernize.Logic.Spatial.ProAudioDuplexConfig cfg = store.BuildProAudioConfig();
        audioOutput?.ApplyProAudioConfig(cfg);
        if (cfg.Enabled && cfg.LiveMicEnabled && audioOutput != null) {
            audioOutput.Start();
        }
        RefreshProAudioUi();
    }

    void RefreshProAudioUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (proAudioEnabledSwitch != null && proAudioEnabledSwitch.Checked != store.ProAudioEnabled) {
            proAudioEnabledSwitch.Checked = store.ProAudioEnabled;
        }
        if (proAudioLiveMicSwitch != null && proAudioLiveMicSwitch.Checked != store.LiveMicEnabled) {
            proAudioLiveMicSwitch.Checked = store.LiveMicEnabled;
        }
        if (proAudioPathSelector != null) {
            int pIdx = Math.Clamp(store.ProAudioPreferredPathIndex, 0, 3);
            if (proAudioPathSelector.SelectedIndex != pIdx) {
                proAudioPathSelector.SetSelection(pIdx, notify: false);
            }
        }
        if (proAudioBufferSelector != null) {
            int bIdx = store.ProAudioBufferFrames <= 64 ? 0 : (store.ProAudioBufferFrames <= 128 ? 1 : 2);
            if (proAudioBufferSelector.SelectedIndex != bIdx) {
                proAudioBufferSelector.SetSelection(bIdx, notify: false);
            }
        }
        if (proAudioSrSelector != null) {
            int srIdx = Array.IndexOf(ProAudioSampleRateValues, store.ProAudioLockedSampleRate);
            int safeSrIdx = srIdx >= 0 ? srIdx : 0;
            if (proAudioSrSelector.SelectedIndex != safeSrIdx) {
                proAudioSrSelector.SetSelection(safeSrIdx, notify: false);
            }
        }
        if (proAudioMicRouteSelector != null) {
            int mIdx = Math.Clamp(store.LiveMicRoutingIndex, 0, 3);
            if (proAudioMicRouteSelector.SelectedIndex != mIdx) {
                proAudioMicRouteSelector.SetSelection(mIdx, notify: false);
            }
        }
        SyncSlider(proAudioMicGainBar, (int)MathF.Round(store.LiveMicInputGainDb), -18, 24, " dB");
        SyncSlider(proAudioMicMonitorBar, (int)MathF.Round(store.LiveMicMonitorLevel * 100f), 0, 125, "%");

        Cavernize.Logic.Spatial.ProAudioDuplexDiagnostics diag =
            audioOutput?.ProAudioDiagnostics ?? Cavernize.Logic.Spatial.ProAudioDuplexDiagnostics.Default;
        UsbProAudioDeviceSnapshot usbSnap = usbProAudioManager?.CurrentSnapshot;

        if (proAudioStatusText != null) {
            string backendLabel = diag.ActiveBackend switch {
                Cavernize.Logic.Spatial.ActiveProAudioBackendKind.LibUsbUac2Direct => S["ProAudioBackendPathB"],
                Cavernize.Logic.Spatial.ActiveProAudioBackendKind.AAudioOboeFullDuplex => S["ProAudioBackendPathA"],
                _ => S["ProAudioBackendLegacy"]
            };
            string calTag = diag.IsLatencyCalibrated ? S["ProAudioCalibratedTag"] : string.Empty;
            string nextStatus = string.Format(
                S["ProAudioStatusFormat"],
                backendLabel,
                diag.SampleRate / 1000.0,
                diag.BitDepth,
                diag.BufferFrames,
                diag.RoundTripLatencyMs,
                calTag);
            if (!string.Equals(proAudioStatusText.Text, nextStatus, StringComparison.Ordinal)) {
                proAudioStatusText.Text = nextStatus;
                proAudioStatusText.SetTextColor(
                    diag.ActiveBackend == Cavernize.Logic.Spatial.ActiveProAudioBackendKind.LibUsbUac2Direct
                        ? OneUiTheme.IconSystemInfo
                        : (diag.ActiveBackend == Cavernize.Logic.Spatial.ActiveProAudioBackendKind.AAudioOboeFullDuplex
                            ? OneUiTheme.Primary
                            : OneUiTheme.TextSub));
            }
        }

        if (proAudioTelemetryText != null) {
            string nextTel;
            Color nextCol;
            if (usbSnap != null && usbSnap.IsAttached && !usbSnap.HasPermission) {
                nextTel = string.Format(S["ProAudioUsbWaitingPerm"], usbSnap.DisplayName);
                nextCol = OneUiTheme.PeachText;
            } else if (!store.ProAudioEnabled && (usbSnap == null || !usbSnap.IsAttached)) {
                nextTel = S["ProAudioNoUsbAttached"];
                nextCol = OneUiTheme.TextMuted;
            } else {
                string syncMode = string.IsNullOrEmpty(diag.ClockSyncMode) ? "-" : diag.ClockSyncMode;
                nextTel = string.Format(
                    S["ProAudioTelemetryFormat"],
                    syncMode,
                    diag.AsyncClockDriftPpm,
                    diag.OutputXRuns,
                    diag.InputXRuns,
                    diag.MicPeakDbfs);
                nextCol = OneUiTheme.TextMuted;
            }
            if (!string.Equals(proAudioTelemetryText.Text, nextTel, StringComparison.Ordinal)) {
                proAudioTelemetryText.Text = nextTel;
                proAudioTelemetryText.SetTextColor(nextCol);
            }
        }
        RefreshLyricsSingDockUi();
    }

    // Slot 2D: W11/W14 Auto-Vocal Mix-Match & Live Mastering Vocal Chain Modal + Persistence Sync
    void ApplyAndRefreshVocalChain() {
        if (store == null || playerEngine == null) {
            return;
        }
        Cavern.VocalChain.VocalMacroSettings macros = store.BuildVocalMacroSettings();
        macros.Bypass = playerEngine.VocalChainBypassed;
        playerEngine.VocalMixMatchStage.ApplyVocalProfile(playerEngine.VocalMixMatchStage.ActiveDna, macros);
        RefreshLyricsSingDockUi();
        RefreshSpatialXRayHud();
    }

    void ShowAutoVocalMixMatchModal() {
        if (store == null || playerEngine == null) {
            return;
        }
        const float VoiceprintDurationSec = 4.0f;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView telLabel = Label("", 11.2f, OneUiTheme.IconSystemInfo);
        telLabel.SetPadding(0, 0, 0, Dp(8));
        content.AddView(telLabel);

        // Song Vocal DNA & Mic Voiceprint readout card (W19.b)
        LinearLayout dnaCard = new(this) { Orientation = Orientation.Vertical };
        dnaCard.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceSubTile, 16f, OneUiTheme.BorderSubtle, 0.9f);
        dnaCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        TextView dnaTitleTv = Label(S["VocalDnaSectionTitle"], 12.2f, OneUiTheme.TextPrimary);
        dnaTitleTv.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        dnaCard.AddView(dnaTitleTv);

        TextView dnaSummaryTv = Label("", 11.2f, OneUiTheme.TextSub);
        dnaSummaryTv.SetPadding(0, Dp(4), 0, 0);
        dnaCard.AddView(dnaSummaryTv);

        TextView vpStatusTv = Label("", 11.2f, OneUiTheme.TextMuted);
        vpStatusTv.SetPadding(0, Dp(4), 0, Dp(8));
        dnaCard.AddView(vpStatusTv);

        // Action row 1: [Analyze Song DNA] [Calibrate Mic (4s)]
        LinearLayout dnaActionRow = HRow();
        dnaActionRow.BaselineAligned = false;
        dnaActionRow.SetGravity(GravityFlags.CenterVertical);

        Button analyzeDnaBtn = null;
        Button calibrateMicBtn = null;
        Button resetVpBtn = null;
        Button abCompareBtn = null;
        Switch bypassSwitchRef = null;
        bool dialogClosed = false;

        void RefreshModalLiveState() {
            if (uiDestroyed || dialogClosed || playerEngine == null || store == null) {
                return;
            }
            Cavern.VocalChain.VocalChainTelemetry tel = playerEngine.SceneHub.VocalChainDiagnostics;
            telLabel.Text = string.Format(
                S["VocalLiveTelemetryFormat"],
                tel.InputDbfs,
                tel.OutputDbfs,
                tel.CompressorReductionDb,
                tel.DeEsserReductionDb,
                tel.SidechainPocketDuckDb);

            Cavern.VocalChain.VocalMixDnaProfile dna = playerEngine.VocalMixMatchStage.ActiveDna;
            if (dna.IsExtractedFromSong) {
                string delayTag = dna.DelayTimeMs > 1f && dna.DelayWetMix > 0.005f
                    ? string.Format(S["VocalDnaDelayTagFmt"], dna.DelayTimeMs)
                    : string.Empty;
                string drrTag = dna.HasMeasuredDereverb && float.IsFinite(dna.MeasuredDrrDb)
                    ? string.Format(S["VocalDnaDrrTagFmt"], dna.MeasuredDrrDb)
                    : string.Empty;
                string trackLabel = string.IsNullOrWhiteSpace(dna.SourceTrackSummary)
                    ? S["TabMusicPlayer"]
                    : dna.SourceTrackSummary;
                dnaSummaryTv.Text = string.Format(
                    S["VocalDnaSummaryFmt"],
                    trackLabel,
                    dna.TargetVocalLufs,
                    dna.CrestFactorDb,
                    dna.ReverbRt60Sec,
                    dna.ReverbWetMix * 100f,
                    delayTag,
                    drrTag);
                dnaSummaryTv.SetTextColor(OneUiTheme.FunctionalGreen);
            } else {
                dnaSummaryTv.Text = S["VocalDnaNeutralStatus"];
                dnaSummaryTv.SetTextColor(OneUiTheme.TextSub);
            }

            bool capturing = playerEngine.LiveMicBridge.IsCapturingVoiceprint;
            if (capturing) {
                float progress = playerEngine.LiveMicBridge.VoiceprintCaptureProgress;
                float remSec = Math.Max(0f, VoiceprintDurationSec * (1f - progress));
                float livePeak = audioOutput?.ProAudioDiagnostics.MicPeakDbfs ?? tel.InputDbfs;
                vpStatusTv.Text = string.Format(S["VocalVoiceprintCapturingFmt"], progress * 100f, remSec, livePeak);
                vpStatusTv.SetTextColor(OneUiTheme.FunctionalOrange);
                if (calibrateMicBtn != null) {
                    calibrateMicBtn.Text = string.Format(S["VocalVoiceprintCancelBtn"], remSec);
                    calibrateMicBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.FunctionalOrange, OneUiTheme.PillCornerDp);
                }
            } else {
                bool hasVp = playerEngine.VocalMixMatchStage.HasMicVoiceprint;
                float[] deltas = playerEngine.VocalMixMatchStage.GetActiveMatchEqDeltasDb();
                if (hasVp && deltas != null && deltas.Length >= 8) {
                    float low = (deltas[0] + deltas[1]) * 0.5f;
                    float mid = (deltas[2] + deltas[3] + deltas[4] + deltas[5]) * 0.25f;
                    float air = (deltas[6] + deltas[7]) * 0.5f;
                    vpStatusTv.Text = string.Format(S["VocalVoiceprintActiveFmt"], low, mid, air);
                    vpStatusTv.SetTextColor(OneUiTheme.IconSystemInfo);
                } else if (hasVp) {
                    vpStatusTv.Text = S["VocalVoiceprintWaitingSongDna"];
                    vpStatusTv.SetTextColor(OneUiTheme.IconSystemInfo);
                } else {
                    vpStatusTv.Text = S["VocalVoiceprintUncalibrated"];
                    vpStatusTv.SetTextColor(OneUiTheme.TextMuted);
                }
                if (calibrateMicBtn != null) {
                    calibrateMicBtn.Text = S["VocalVoiceprintCalibrateBtn"];
                    calibrateMicBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                        this,
                        hasVp ? OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 76) : OneUiTheme.SurfaceCard,
                        OneUiTheme.PillCornerDp);
                }
            }

            if (analyzeDnaBtn != null) {
                analyzeDnaBtn.Text = isNeuralPrepRunning
                    ? string.Format(S["VocalDnaBusyBtn"], neuralPrepProgress)
                    : S["VocalDnaAnalyzeBtn"];
                analyzeDnaBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                    this,
                    isNeuralPrepRunning
                        ? OneUiTheme.FunctionalOrange
                        : (dna.IsExtractedFromSong ? OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 76) : OneUiTheme.SurfaceCard),
                    OneUiTheme.PillCornerDp);
            }

            if (resetVpBtn != null) {
                resetVpBtn.Visibility = playerEngine.VocalMixMatchStage.HasMicVoiceprint && !capturing
                    ? ViewStates.Visible
                    : ViewStates.Gone;
            }

            if (abCompareBtn != null) {
                bool dryBypass = playerEngine.VocalChainBypassed;
                abCompareBtn.Text = dryBypass ? S["VocalAbStateDry"] : S["VocalAbStateWet"];
                abCompareBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                    this,
                    dryBypass ? OneUiTheme.FunctionalOrange : OneUiTheme.Primary,
                    OneUiTheme.PillCornerDp);
                abCompareBtn.SetTextColor(Color.White);
            }
            if (bypassSwitchRef != null && bypassSwitchRef.Checked != playerEngine.VocalChainBypassed) {
                bypassSwitchRef.Checked = playerEngine.VocalChainBypassed;
            }
        }

        analyzeDnaBtn = PillButton(S["VocalDnaAnalyzeBtn"], () => {
            PrepareSongVocalDnaAsync();
            RefreshModalLiveState();
        });
        analyzeDnaBtn.TextSize = 11.4f;
        analyzeDnaBtn.SetSingleLine(true);
        analyzeDnaBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        dnaActionRow.AddView(analyzeDnaBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        calibrateMicBtn = PillButton(S["VocalVoiceprintCalibrateBtn"], () => {
            if (playerEngine == null) {
                return;
            }
            if (playerEngine.LiveMicBridge.IsCapturingVoiceprint) {
                playerEngine.CancelMicVoiceprintCalibration();
                RefreshModalLiveState();
                return;
            }
            RequestRecordAudioPermissionIfNeeded();
            if (!store.ProAudioEnabled) {
                store.ProAudioEnabled = true;
            }
            if (!store.LiveMicEnabled) {
                store.LiveMicEnabled = true;
            }
            if (store.LiveMicRoutingIndex == 0) {
                store.LiveMicRoutingIndex = 1; // MonoChannel1
            }
            ApplyAndRefreshProAudio();
            playerEngine?.StartMicVoiceprintCalibration(VoiceprintDurationSec, result => RunOnUiThread(() => {
                if (uiDestroyed || playerEngine == null) {
                    return;
                }
                if (result.IsValid && result.Profile != null) {
                    store.SaveMicVoiceprintBands(result.Profile.BandEnergyDb);
                    Cavern.VocalChain.VocalMacroSettings macros = store.BuildVocalMacroSettings();
                    macros.Bypass = playerEngine.VocalChainBypassed;
                    playerEngine.VocalMixMatchStage.ApplyMicVoiceprint(result.Profile.BandEnergyDb, macros);
                    RefreshLyricsSingDockUi();
                    RefreshSpatialXRayHud();
                    ShowToast(string.Format(
                        S["VocalVoiceprintSuccessToast"],
                        result.ActiveRmsDbfs,
                        result.ActiveWindowCount));
                } else {
                    ShowToast(string.Format(S["VocalVoiceprintTooQuietFmt"], result.ActiveRmsDbfs));
                }
                RefreshModalLiveState();
            }));
            RefreshModalLiveState();
        });
        calibrateMicBtn.TextSize = 11.4f;
        calibrateMicBtn.SetSingleLine(true);
        calibrateMicBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        dnaActionRow.AddView(calibrateMicBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        dnaCard.AddView(dnaActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Action row 2: [Instant A/B Compare] [Reset Mic]
        LinearLayout abActionRow = HRow();
        abActionRow.BaselineAligned = false;
        abActionRow.SetGravity(GravityFlags.CenterVertical);
        abActionRow.SetPadding(0, Dp(6), 0, 0);

        abCompareBtn = PillButton(S["VocalAbStateWet"], () => {
            if (playerEngine == null) {
                return;
            }
            playerEngine.VocalChainBypassed = !playerEngine.VocalChainBypassed;
            ApplyAndRefreshVocalChain();
            RefreshModalLiveState();
        });
        abCompareBtn.TextSize = 11.6f;
        abCompareBtn.SetSingleLine(true);
        abCompareBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        abActionRow.AddView(abCompareBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        resetVpBtn = PillButton(S["VocalVoiceprintResetBtn"], () => {
            if (playerEngine == null) {
                return;
            }
            store.ClearMicVoiceprint();
            Cavern.VocalChain.VocalMacroSettings macros = store.BuildVocalMacroSettings();
            macros.Bypass = playerEngine.VocalChainBypassed;
            playerEngine.VocalMixMatchStage.ClearMicVoiceprint(macros);
            RefreshLyricsSingDockUi();
            RefreshSpatialXRayHud();
            ShowToast(S["VocalVoiceprintResetToast"]);
            RefreshModalLiveState();
        });
        resetVpBtn.TextSize = 11.4f;
        resetVpBtn.SetSingleLine(true);
        resetVpBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        abActionRow.AddView(resetVpBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(6) });
        dnaCard.AddView(abActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        content.AddView(dnaCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(content, S["ProAudioLiveMicTitle"], store.LiveMicEnabled, v => {
            store.LiveMicEnabled = v;
            if (v) {
                RequestRecordAudioPermissionIfNeeded();
                if (!store.ProAudioEnabled) {
                    store.ProAudioEnabled = true;
                }
                if (store.LiveMicRoutingIndex == 0) {
                    store.LiveMicRoutingIndex = 1;
                }
            }
            ApplyAndRefreshProAudio();
            RefreshModalLiveState();
        });
        AddDivider(content);

        AddSwitchRow(content, S["VocalEnableTitle"], store.VocalMixMatchEnabled, v => {
            store.VocalMixMatchEnabled = v;
            ApplyAndRefreshVocalChain();
            RefreshModalLiveState();
        });
        AddDivider(content);

        bypassSwitchRef = AddSwitchRow(content, S["VocalBypassAbTitle"], playerEngine?.VocalChainBypassed ?? false, v => {
            if (playerEngine != null) {
                playerEngine.VocalChainBypassed = v;
            }
            ApplyAndRefreshVocalChain();
            RefreshModalLiveState();
        });
        AddDivider(content);

        AddSliderRow(
            content,
            S["VocalMixMatchPctTitle"],
            0,
            100,
            Math.Clamp((int)MathF.Round(store.VocalMixMatchPct), 0, 100),
            v => {
                store.VocalMixMatchPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 75);

        AddSliderRow(
            content,
            S["VocalReverbPctTitle"],
            0,
            150,
            Math.Clamp((int)MathF.Round(store.VocalReverbPct), 0, 150),
            v => {
                store.VocalReverbPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 100);

        AddSliderRow(
            content,
            S["VocalDynamicsPctTitle"],
            0,
            150,
            Math.Clamp((int)MathF.Round(store.VocalDynamicsPct), 0, 150),
            v => {
                store.VocalDynamicsPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 100);

        AddSliderRow(
            content,
            S["VocalWarmthPctTitle"],
            0,
            100,
            Math.Clamp((int)MathF.Round(store.VocalWarmthPct), 0, 100),
            v => {
                store.VocalWarmthPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 35);

        AddSliderRow(
            content,
            S["VocalLevelDbTitle"],
            -18,
            12,
            Math.Clamp((int)MathF.Round(store.VocalLevelDb), -18, 12),
            v => {
                store.VocalLevelDb = v;
                ApplyAndRefreshVocalChain();
            },
            " dB",
            defaultValue: 0);

        AddDivider(content);
        AddSwitchRow(content, S["VocalSidechainGlueTitle"], store.VocalSidechainPocketGlue, v => {
            store.VocalSidechainPocketGlue = v;
            ApplyAndRefreshVocalChain();
        });
        AddDivider(content);
        AddSwitchRow(content, S["VocalNoiseGateTitle"], store.VocalNoiseGate, v => {
            store.VocalNoiseGate = v;
            ApplyAndRefreshVocalChain();
        });
        AddDivider(content);
        AddSwitchRow(content, S["VocalDeEsserTitle"], store.VocalDeEsser, v => {
            store.VocalDeEsser = v;
            ApplyAndRefreshVocalChain();
        });

        LinearLayout scarlettRow = HRow();
        scarlettRow.BaselineAligned = false;
        scarlettRow.SetGravity(GravityFlags.CenterVertical);
        scarlettRow.SetPadding(0, Dp(8), 0, 0);
        Button scarlettQuickBtn = PillButton(S["ScarlettControlsBtn"], ShowScarlett2i2ControlsModal);
        scarlettQuickBtn.SetSingleLine(true);
        scarlettQuickBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        scarlettRow.AddView(scarlettQuickBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(scarlettRow);

        RefreshModalLiveState();

        Android.App.Dialog dlg = OneUiModalDialog.Show(
            this,
            S["VocalModalTitle"],
            customContent: content,
            icon: OneUiIconType.Microphone,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["CloseBtn"],
            onHelp: () => ShowModalGuidedHelp(S["VocalModalHelpTitle"], S["VocalModalHelpBody"], OneUiIconType.Microphone),
            helpContentDesc: S["VocalModalHelpTitle"]);

        Java.Lang.Runnable pollRunnable = null;
        pollRunnable = new Java.Lang.Runnable(() => {
            if (dialogClosed || uiDestroyed) {
                return;
            }
            RefreshModalLiveState();
            uiHandler?.PostDelayed(pollRunnable, 250);
        });
        uiHandler?.PostDelayed(pollRunnable, 250);

        if (dlg != null) {
            dlg.DismissEvent += (_, _) => {
                dialogClosed = true;
                if (pollRunnable != null) {
                    uiHandler?.RemoveCallbacks(pollRunnable);
                }
                if (playerEngine?.LiveMicBridge?.IsCapturingVoiceprint == true) {
                    playerEngine.CancelMicVoiceprintCalibration();
                }
            };
        }
    }

    void ShowCalibrateRttModal() {
        OneUiModalDialog.Show(
            this,
            S["ProAudioCalibrateModalTitle"],
            S["ProAudioCalibrateModalBody"],
            icon: OneUiIconType.Bolt,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["ProAudioCalibrateStartBtn"],
            onPositive: () => {
                RequestRecordAudioPermissionIfNeeded();
                if (!store.ProAudioEnabled) {
                    store.ProAudioEnabled = true;
                }
                audioOutput?.ApplyProAudioConfig(store.BuildProAudioConfig());
                audioOutput?.Start();
                ShowToast(S["ProAudioCalibrateRunningToast"]);
                playerEngine?.StartRoundTripLatencyCalibration(result => RunOnUiThread(() => {
                    if (uiDestroyed) {
                        return;
                    }
                    if (result.IsValid) {
                        store.CalibratedRoundTripSamples = (float)result.LatencySamples;
                        store.CalibratedRoundTripMs = (float)result.LatencyMs;
                        ApplyAndRefreshProAudio();
                        ShowToast(string.Format(
                            S["ProAudioCalibrateSuccessToast"],
                            result.LatencyMs,
                            result.LatencySamples,
                            result.ConfidenceSnr));
                    } else {
                        ShowToast(string.Format(S["ProAudioCalibrateFailedToast"], result.ConfidenceSnr));
                    }
                }));
            },
            negativeText: S["CancelBtn"]);
    }

    void ShowScarlett2i2ControlsModal() {
        UsbProAudioDeviceSnapshot snap = usbProAudioManager?.ScanAttachedDevices(autoOpenIfPermitted: true);
        ScarlettHardwareState st = usbProAudioManager?.ScarlettState;

        Action showScarlettHelp = () => ShowModalGuidedHelp(S["ScarlettHelpTitle"], S["ScarlettHelpBody"], OneUiIconType.Sliders);

        if (snap == null || !snap.IsAttached || !snap.HasPermission || st == null || st.Generation == ScarlettGeneration.None) {
            OneUiModalDialog.Show(
                this,
                S["ScarlettModalTitle"],
                S["ScarlettNotConnectedMsg"],
                icon: OneUiIconType.Sliders,
                iconBadgeColor: OneUiTheme.IconSpatial,
                positiveText: S["ProAudioGrantUsbBtn"],
                onPositive: () => {
                    usbProAudioManager?.RequestUsbPermission();
                    RefreshProAudioUi();
                },
                negativeText: S["CloseBtn"],
                onHelp: showScarlettHelp,
                helpContentDesc: S["ScarlettHelpTitle"]);
            return;
        }

        if (st.Generation == ScarlettGeneration.Gen2) {
            OneUiModalDialog.Show(
                this,
                st.ModelName,
                S["ScarlettGen2Notice"],
                icon: OneUiIconType.Sliders,
                iconBadgeColor: OneUiTheme.IconSpatial,
                positiveText: S["CloseBtn"],
                onHelp: showScarlettHelp,
                helpContentDesc: S["ScarlettHelpTitle"]);
            return;
        }

        if (st.Generation == ScarlettGeneration.Gen4 && !st.IsGen4FcpControlTableVerified) {
            LinearLayout gen4Content = new(this) { Orientation = Orientation.Vertical };
            TextView gen4Hdr = Label($"{st.ModelName} (PID 0x{st.ProductId:X4})", 13f, OneUiTheme.Primary);
            gen4Hdr.Typeface = OneUiTheme.GetAppleFontBold(this);
            gen4Content.AddView(gen4Hdr, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });

            TextView fcpNotice = Label(S["ScarlettGen4FcpNotice"], 11.6f, OneUiTheme.TextSub);
            gen4Content.AddView(fcpNotice, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(10)
            });

            float livePeak = playerEngine?.LiveMicBridge?.PeakDbfs ?? -120f;
            TextView assistReadout = Label(
                string.Format(S["ScarlettMetersFormat"], livePeak, livePeak),
                11.4f,
                OneUiTheme.TextSub);
            gen4Content.AddView(assistReadout, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });

            LinearLayout gen4BtnRow = HRow();
            Button gainAssistBtn = PillButton(S["ScarlettGainAssistBtn"], () => {
                float curPeak = playerEngine?.LiveMicBridge?.PeakDbfs ?? -120f;
                if (curPeak <= -115f) {
                    ShowToast(S["ScarlettGainAssistNoSignalToast"]);
                    return;
                }
                float rec1 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(0, curPeak);
                float rec2 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(1, curPeak);
                assistReadout.Text = string.Format(S["ScarlettGainAssistFormat"], rec1, rec2);
            }, active: true);
            gen4BtnRow.AddView(gainAssistBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            gen4Content.AddView(gen4BtnRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            OneUiModalDialog.Show(
                this,
                S["ScarlettModalTitle"],
                customContent: gen4Content,
                icon: OneUiIconType.Sliders,
                iconBadgeColor: OneUiTheme.IconSpatial,
                positiveText: S["CloseBtn"],
                onHelp: showScarlettHelp,
                helpContentDesc: S["ScarlettHelpTitle"]);
            return;
        }

        usbProAudioManager.TryInitializeScarlettControl(exitMsdMode: false);
        usbProAudioManager.ReadScarlettHardwareState();
        st = usbProAudioManager.ScarlettState;

        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        TextView modelHdr = Label($"{st.ModelName} (PID 0x{st.ProductId:X4})", 13f, OneUiTheme.Primary);
        modelHdr.Typeface = OneUiTheme.GetAppleFontBold(this);
        content.AddView(modelHdr, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(4)
        });

        TextView safetyNote = Label(S["Scarlett48vSafetyNote"], 11.2f, OneUiTheme.PeachText);
        content.AddView(safetyNote, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        bool linked = st.InputsLinked;
        if (st.SupportsStereoLink) {
            AddSwitchRow(content, S["ScarlettLinkTitle"], linked, v => {
                linked = v;
                usbProAudioManager.SetScarlettGen4InputLink(v);
            });
            AddDivider(content);
        }

        bool ch1Phantom = st.PhantomPowerCh1;
        bool ch2Phantom = st.PhantomPowerCh2;
        AddSwitchRow(content, S["Scarlett48vCh1Title"], ch1Phantom, v => {
            ch1Phantom = v;
            if (!st.SupportsIndependentPreampGain || linked) {
                ch2Phantom = v;
            }
            usbProAudioManager.SetScarlettPhantomPower(ch1Phantom, ch2Phantom);
        });

        if (st.SupportsIndependentPreampGain) {
            AddDivider(content);
            AddSwitchRow(content, S["Scarlett48vCh2Title"], ch2Phantom, v => {
                ch2Phantom = v;
                if (linked) {
                    ch1Phantom = v;
                }
                usbProAudioManager.SetScarlettPhantomPower(ch1Phantom, ch2Phantom);
            });
        }

        AddDivider(content);
        ScarlettAirMode air1 = st.AirCh1;
        ScarlettAirMode air2 = st.AirCh2;
        string[] airLabels = st.Generation == ScarlettGeneration.Gen4
            ? [S["ScarlettAirOff"], S["ScarlettAirPresence"], S["ScarlettAirDrive"]]
            : [S["ScarlettAirOff"], S["ScarlettAirPresence"]];

        OneUiInlineSelector air1Sel = new(this, S["ScarlettAirCh1Title"]);
        air1Sel.SetItems(airLabels, Math.Clamp((int)air1, 0, airLabels.Length - 1));
        air1Sel.SelectionChanged += pos => {
            air1 = (ScarlettAirMode)pos;
            if (linked) {
                air2 = air1;
            }
            usbProAudioManager.SetScarlettAirMode(air1, air2);
        };
        content.AddView(air1Sel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(6)
        });

        OneUiInlineSelector air2Sel = new(this, S["ScarlettAirCh2Title"]);
        air2Sel.SetItems(airLabels, Math.Clamp((int)air2, 0, airLabels.Length - 1));
        air2Sel.SelectionChanged += pos => {
            air2 = (ScarlettAirMode)pos;
            if (linked) {
                air1 = air2;
            }
            usbProAudioManager.SetScarlettAirMode(air1, air2);
        };
        content.AddView(air2Sel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        bool inst1 = st.InstrumentModeCh1;
        bool inst2 = st.InstrumentModeCh2;
        AddSwitchRow(content, S["ScarlettInstCh1Title"], inst1, v => {
            inst1 = v;
            if (linked) {
                inst2 = v;
            }
            usbProAudioManager.SetScarlettInstrumentMode(inst1, inst2);
        });
        AddDivider(content);
        AddSwitchRow(content, S["ScarlettInstCh2Title"], inst2, v => {
            inst2 = v;
            if (linked) {
                inst1 = v;
            }
            usbProAudioManager.SetScarlettInstrumentMode(inst1, inst2);
        });

        AddDivider(content);
        OneUiInlineSelector dmSel = new(this, S["ScarlettDirectMonTitle"]);
        string[] dmLabels = [S["ScarlettDirectMonOff"], S["ScarlettDirectMonMono"], S["ScarlettDirectMonStereo"]];
        dmSel.SetItems(dmLabels, Math.Clamp((int)st.DirectMonitor, 0, dmLabels.Length - 1));
        dmSel.SelectionChanged += pos => {
            usbProAudioManager.SetScarlettDirectMonitor((ScarlettDirectMonitorMode)pos);
        };
        content.AddView(dmSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(6)
        });

        if (st.SupportsIndependentPreampGain) {
            float g1 = st.InputGainCh1Db;
            float g2 = st.InputGainCh2Db;
            bool safe1 = st.ClipSafeCh1;
            bool safe2 = st.ClipSafeCh2;
            bool outMute = st.OutputMute;
            bool outDim = st.OutputDim;

            AddDivider(content);
            AddSliderRow(content, S["ScarlettGainCh1Title"], 0, 69, (int)MathF.Round(g1), v => {
                g1 = v;
                if (linked) {
                    g2 = v;
                }
                usbProAudioManager.SetScarlettGen4PreampGain(g1, g2);
            }, " dB", defaultValue: 24);
            AddSliderRow(content, S["ScarlettGainCh2Title"], 0, 69, (int)MathF.Round(g2), v => {
                g2 = v;
                if (linked) {
                    g1 = v;
                }
                usbProAudioManager.SetScarlettGen4PreampGain(g1, g2);
            }, " dB", defaultValue: 24);

            // Gen 4 Hardware Auto Gain controls (Ch 1, Ch 2, Cancel + live status)
            TextView autoGainStatusLabel = Label(
                string.Format(S["ScarlettAutoGainStatusFormat"], st.AutoGainStatusCh1, st.AutoGainStatusCh2),
                11.2f,
                OneUiTheme.TextSub);
            content.AddView(autoGainStatusLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(4),
                BottomMargin = Dp(4)
            });

            LinearLayout autoRow = HRow();
            Button auto1Btn = PillButton(S["ScarlettAutoGainCh1Btn"], () => {
                if (usbProAudioManager.StartScarlettGen4AutoGain(linked ? -1 : 0)) {
                    autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"],
                        usbProAudioManager.ScarlettState.AutoGainStatusCh1,
                        usbProAudioManager.ScarlettState.AutoGainStatusCh2);
                    ShowToast(S["ScarlettAutoGainRunningToast"]);
                }
            }, active: true);
            Button auto2Btn = PillButton(S["ScarlettAutoGainCh2Btn"], () => {
                if (usbProAudioManager.StartScarlettGen4AutoGain(linked ? -1 : 1)) {
                    autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"],
                        usbProAudioManager.ScarlettState.AutoGainStatusCh1,
                        usbProAudioManager.ScarlettState.AutoGainStatusCh2);
                    ShowToast(S["ScarlettAutoGainRunningToast"]);
                }
            });
            Button autoStopBtn = PillButton(S["ScarlettAutoGainCancelBtn"], () => {
                usbProAudioManager.CancelScarlettGen4AutoGain();
                usbProAudioManager.PollScarlettGen4AutoGainStatus(out ScarlettAutoGainStatus s1, out ScarlettAutoGainStatus s2);
                autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"], s1, s2);
            });
            autoRow.AddView(auto1Btn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
            autoRow.AddView(auto2Btn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
            autoRow.AddView(autoStopBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.85f));
            content.AddView(autoRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });

            AddSwitchRow(content, S["ScarlettClipSafeCh1Title"], safe1, v => {
                safe1 = v;
                if (linked) {
                    safe2 = v;
                }
                usbProAudioManager.SetScarlettGen4ClipSafe(safe1, safe2);
            });
            AddDivider(content);
            AddSwitchRow(content, S["ScarlettClipSafeCh2Title"], safe2, v => {
                safe2 = v;
                if (linked) {
                    safe1 = v;
                }
                usbProAudioManager.SetScarlettGen4ClipSafe(safe1, safe2);
            });
            AddDivider(content);
            AddSliderRow(content, S["ScarlettDirectMonMixTitle"], 0, 100, st.DirectMonitorMixPct, v => {
                usbProAudioManager.SetScarlettDirectMonitorMix(v);
            }, "%", defaultValue: 50);
            AddSwitchRow(content, S["ScarlettOutputMuteTitle"], outMute, v => {
                outMute = v;
                usbProAudioManager.SetScarlettOutputMuteDim(outMute, outDim);
            });
            AddDivider(content);
            AddSwitchRow(content, S["ScarlettOutputDimTitle"], outDim, v => {
                outDim = v;
                usbProAudioManager.SetScarlettOutputMuteDim(outMute, outDim);
            });
            AddDivider(content);
            AddSwitchRow(content, S["ScarlettLoopbackTitle"], st.LoopbackEnabled, v => {
                usbProAudioManager.SetScarlettLoopback(v);
            });
        }

        TextView meterLabel = Label(
            string.Format(S["ScarlettMetersFormat"], st.InputMeterCh1Dbfs, st.InputMeterCh2Dbfs),
            11.4f,
            OneUiTheme.TextSub);
        content.AddView(meterLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6),
            BottomMargin = Dp(4)
        });

        TextView gainAssistLabel = Label(string.Empty, 11.2f, OneUiTheme.Primary);
        gainAssistLabel.Visibility = ViewStates.Gone;
        content.AddView(gainAssistLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout actionRow = HRow();
        Button metersBtn = PillButton(S["ScarlettReadMetersBtn"], () => {
            if (usbProAudioManager.PollScarlettHardwareMeters(out float m1, out float m2)) {
                meterLabel.Text = string.Format(S["ScarlettMetersFormat"], m1, m2);
            }
        }, active: true);
        Button assistBtn = PillButton(S["ScarlettGainAssistBtn"], () => {
            usbProAudioManager.PollScarlettHardwareMeters(out _, out _);
            float fallbackPeak = playerEngine?.LiveMicBridge?.PeakDbfs ?? -120f;
            ScarlettHardwareState latest = usbProAudioManager.ScarlettState;
            if (latest.InputMeterCh1Dbfs <= -115f && latest.InputMeterCh2Dbfs <= -115f && fallbackPeak <= -115f) {
                ShowToast(S["ScarlettGainAssistNoSignalToast"]);
                return;
            }
            float rec1 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(0, fallbackPeak);
            float rec2 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(1, fallbackPeak);
            gainAssistLabel.Text = string.Format(S["ScarlettGainAssistFormat"], rec1, rec2);
            gainAssistLabel.Visibility = ViewStates.Visible;
        });
        actionRow.AddView(metersBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        actionRow.AddView(assistBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(actionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        LinearLayout nvramRow = HRow();
        Button saveBtn = PillButton(S["ScarlettSaveNvramBtn"], () => {
            if (usbProAudioManager.SaveScarlettConfigToNvram()) {
                ShowToast(S["ScarlettSaveNvramDoneToast"]);
            }
        });
        Button msdBtn = PillButton(S["ScarlettExitMsdBtn"], () => {
            if (usbProAudioManager.TryInitializeScarlettControl(exitMsdMode: true)) {
                ShowToast(S["ScarlettExitMsdDoneToast"]);
            }
        });
        nvramRow.AddView(saveBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        nvramRow.AddView(msdBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(nvramRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        OneUiModalDialog.Show(
            this,
            S["ScarlettModalTitle"],
            customContent: content,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["CloseBtn"],
            onHelp: showScarlettHelp,
            helpContentDesc: S["ScarlettHelpTitle"]);
    }

    void ShowProAudioCreditsModal() {
        OneUiModalDialog.Show(
            this,
            S["ProAudioCreditsTitle"],
            Cavernize.Logic.Spatial.ProAudioDuplexDiagnostics.BuildAttributionNotice(),
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"]);
    }
}
