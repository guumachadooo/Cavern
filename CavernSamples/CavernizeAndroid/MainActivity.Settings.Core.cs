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
    TextView logPathInfoText, selfCheckStatusText, omniBridgeStatusText;
    TextView cloudLogStatusText = null, liveBridgeStatusText = null;
    Button selfCheckQuickBtn, selfCheckFullBtn, selfCheckUltraBtn, omniBridgeOpenBtn;
    Button cloudLogUploadBtn = null, liveBridgeToggleBtn = null, liveBridgeCopyBtn = null;
    Switch liveBridgeStandbySwitch = null;
    bool isCloudLogUploading, isSelfCheckRunning, isLiveBridgeBusy;
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
    LinearLayout iso226DetailsSection, loudnessNormDetailsSection, dialogDetailsSection;
    OneUiSeekBar iso226RefSplBar, iso226ListenSplBar, dialogBoostBar;
    OneUiInlineSelector loudnessNormSelector, truePeakCeilingSelector;
    TextView loudnessMeterText, loudnessStatusText;
    TextView neuralRuntimeStatusText, neuralPlanSummaryText, mvsepStatusText, neuralTrackCacheSavingsText;
    Button neuralRuntimeCheckBtn, neuralModelsBtn, mvsepConfigBtn, neuralBrowseTrackCacheBtn;
    OneUiInlineSelector neuralProviderSelector, neuralExecBackendSelector, neuralComputeBudgetSelector, neuralOrchestrationSelector, neuralStudioBundleSelector, neuralEnsembleSelector, neuralUpmixModeSelector, neuralTrackCacheCompSelector;
    OneUiSeekBar neuralOverlapBar, karaokeVocalLevelBar;
    Switch neuralTtaSwitch, neuralPhaseFixSwitch, neuralConservationSwitch, neuralTrackCacheEnabledSwitch;
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
    Switch pedanticSwitch, unsafeSwitch, keepTempSwitch;
    OneUiSeekBar upmixEffectBar, upmixSmoothBar;
    OneUiInlineSelector upmixModeSelector;
    LinearLayout atmosAwareUpmixSection;
    Switch atmosAwareUpmixSwitch;
    Switch settingsRoomInvertOrbitSwitch, roomInvertOrbitSwitch;
    OneUiInlineSelector lyricsHighlightSelector;

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
        settingsModeToggleBtn.SetMinimumWidth(Dp(72));
        settingsModeToggleBtn.SetMinimumHeight(Dp(34));
        settingsModeToggleBtn.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        topRow.AddView(settingsModeToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(topRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

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


        BuildSettingsAudioCards();
        BuildSettingsHeadphonesCards();
        BuildSettingsProAudioCards();
        BuildSettingsSpatialCards();
        BuildSettingsNeuralCards();
        BuildSettingsSystemCards();
        BuildSettingsDiagnosticsCard();
        BuildSettingsAboutCard();

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


    void BuildSettingsSystemCards() {
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

        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
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

    }

    void BuildSettingsAboutCard() {
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
        RegisterSettingsCard(BuildSettingsModularLayoutCard(), categoryIndex: 5, isProOnly: false, "modular layout quick panel reorder customize play room oneui9 blocks");

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
            settingsModeToggleBtn.SetTextColor(isSettingsProMode ? OneUiTheme.OnPrimary : ColText);
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
                    : (store.LoudnessNormModeIndex > 0 ? "BS.1770" : $"TP {store.ResolveTruePeakCeilingDbtp():0.#} dB");
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
                int modelCount = OnnxRuntimeInstallerService.GetCachedInstalledModelCount(this);
                bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
                string ep = playerEngine?.NeuralDiagnostics.ActiveProviderName ?? (installed ? "CPU (ARM64 KleidiAI)" : "Not Installed");
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
                return $"v{verName} · {langTag} · {kb:0.0} KB";
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
            updateSettingsStatusText.Text = string.Format(S["UpdateAvailableBanner"], latestUpdateInfo.RemoteVersion) + $" ({sizeMb:0.0} MB)";
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
            SetLiveTrackSourceItems();
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
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
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
            ConfirmDestructiveAction(
                S["ConfirmDeleteTitle"],
                S["ConfirmRemoveQueueJobMsg"],
                () => {
                    if (isOperationRunning || renderQueue.Jobs.Count == 0) {
                        return;
                    }
                    renderQueue.Jobs.RemoveAt(renderQueue.Jobs.Count - 1);
                    if (renderQueue.Jobs.Count == 0) {
                        protectedQueuePaths.Clear();
                    }
                    RefreshQueueList();
                });
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
}
