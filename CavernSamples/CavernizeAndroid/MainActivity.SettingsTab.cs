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
using Cavern.Format;
using Cavern.Format.Common;
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
    // FFmpeg auto-downloader & updater views
    TextView ffmpegStatusText;
    ProgressBar ffmpegProgressBar;
    Button ffmpegAutoBtn, ffmpegCheckBtn, ffmpegBtn;

    // truehdd auto-downloader & updater views
    TextView truehddStatusText;
    ProgressBar truehddProgressBar;
    Button truehddAutoBtn, truehddCheckBtn, truehddManualBtn;

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
    Button filtersBtn;
    Switch muteBedSwitch, muteGroundSwitch, speakerVirtSwitch, force24Switch, surroundSwapSwitch, wavSkipSwitch, reportModeSwitch, gradingSwitch;
    Switch pedanticSwitch, unsafeSwitch, keepTempSwitch, normSwitch, limiterOnlySwitch;
    OneUiSeekBar upmixEffectBar, upmixSmoothBar;
    Switch matrixUpmixSwitch, cavernizeUpmixSwitch;

    LinearLayout BuildSettingsTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(2), Dp(14), Dp(116));

        // 1. Spatial 3D Upmixing
        LinearLayout upmixCard = Card(S["UpmixTitle"], OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix);
        matrixUpmixSwitch = AddSwitchRow(upmixCard, S["MatrixUpmix"], store.MatrixUpmixing, v => {
            store.MatrixUpmixing = v;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        }, helpBody: S["TipMatrixUpmix"]);
        AddDivider(upmixCard);
        cavernizeUpmixSwitch = AddSwitchRow(upmixCard, S["CavernizeUpmix"], store.CavernizeUpmix, v => {
            store.CavernizeUpmix = v;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        }, helpBody: S["TipCavernizeUpmix"]);
        AddDivider(upmixCard);
        (upmixEffectBar, _) = AddSliderRow(upmixCard, S["UpmixEffect"], 0, 100, (int)Math.Round(store.UpmixEffect * 100), v => {
            store.UpmixEffect = v * 0.01f;
            ReattachPlayerTrack();
        }, "%", defaultValue: 75, helpBody: S["TipUpmixEffect"]);
        (upmixSmoothBar, _) = AddSliderRow(upmixCard, S["UpmixSmooth"], 0, 100, (int)Math.Round(store.UpmixSmoothness * 100), v => {
            store.UpmixSmoothness = v * 0.01f;
            ReattachPlayerTrack();
        }, "%", defaultValue: 80, helpBody: S["TipUpmixSmooth"]);
        Button resetUpmixBtn = PillButton(S["ResetUpmix"], () => {
            store.MatrixUpmixing = false;
            store.CavernizeUpmix = false;
            store.UpmixEffect = 0.75f;
            store.UpmixSmoothness = 0.80f;
            matrixUpmixSwitch.Checked = false;
            cavernizeUpmixSwitch.Checked = false;
            upmixEffectBar.Progress = 75;
            upmixSmoothBar.Progress = 80;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        });
        upmixCard.AddView(resetUpmixBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        page.AddView(upmixCard);

        // 2. Room Acoustics, Gain & Dynamic Limiter
        LinearLayout acousticsCard = Card(S["RoomAcousticsTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        normSwitch = AddSwitchRow(acousticsCard, S["NormalizerCheck"], store.NormalizerEnabled, v => {
            store.NormalizerEnabled = v;
            playerEngine.NormalizerEnabled = v;
        }, helpBody: S["TipNormalizer"]);
        AddDivider(acousticsCard);
        limiterOnlySwitch = AddSwitchRow(acousticsCard, S["LimiterOnlyCheck"], store.LimiterOnly, v => {
            store.LimiterOnly = v;
            playerEngine.LimiterOnly = v;
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
            playerEngine.EnvironmentType = (Environments)pos;
            UpdateRoomCanvas();
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
        Button applyRoomBtn = PillButton("OK", () => {
            if (float.TryParse(rx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float vx) &&
                float.TryParse(ry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float vy) &&
                float.TryParse(rz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float vz)) {
                store.RoomSizeX = Math.Clamp(vx, 1f, 50f);
                store.RoomSizeY = Math.Clamp(vy, 1f, 50f);
                store.RoomSizeZ = Math.Clamp(vz, 1f, 50f);
                store.ApplyGlobals();
                playerEngine.EnvironmentSize = Listener.EnvironmentSize;
                SyncRoomSlidersFromEngine();
                UpdateRoomCanvas();
            }
        }, active: true);
        roomDimRow.AddView(rx, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        roomDimRow.AddView(ry, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        roomDimRow.AddView(rz, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        roomDimRow.AddView(applyRoomBtn, new LinearLayout.LayoutParams(Dp(60), ViewGroup.LayoutParams.WrapContent));
        acousticsCard.AddView(roomDimRow);
        page.AddView(acousticsCard);

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
        page.AddView(rendCard);

        // 4. Collapsible Advanced Rendering & CLI Options
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
            playerEngine.MuteBed = v;
            refreshBentoTilesUi?.Invoke();
        }, helpBody: S["TipMuteBed"]);
        AddDivider(advContentContainer);
        muteGroundSwitch = AddSwitchRow(advContentContainer, S["MuteGround"], store.MuteGround, v => {
            store.MuteGround = v;
            playerEngine.MuteGround = v;
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
            playerEngine.AudioQuality = (QualityModes)pos;
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
        page.AddView(advCard);

        // 5. FFmpeg Audio Engine (Auto-Download & Updater)
        LinearLayout ffCard = Card(S["FFmpegCardTitle"], OneUiIconType.Gear, OneUiTheme.IconSliderMedia);
        ffmpegStatusText = Label(S["FFmpegNotInstalled"], 12.5f, OneUiTheme.TextSub);
        ffmpegStatusText.SetPadding(0, 0, 0, Dp(8));
        ffCard.AddView(ffmpegStatusText);

        ffmpegProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0,
            Visibility = ViewStates.Gone
        };
        OneUiTheme.StyleProgressBar(this, ffmpegProgressBar);
        ffCard.AddView(ffmpegProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)) { BottomMargin = Dp(8) });

        LinearLayout ffBtns = HRow();
        ffmpegAutoBtn = PrimaryButton(S["FFmpegDownloadBtn"], StartFFmpegDownloadOrUpdate);
        ffmpegCheckBtn = PillButton(S["FFmpegCheckBtn"], () => _ = CheckFFmpegUpdateAsync(silent: false));
        ffBtns.AddView(ffmpegAutoBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.35f) { RightMargin = Dp(8) });
        ffBtns.AddView(ffmpegCheckBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        ffCard.AddView(ffBtns);

        AddDivider(ffCard);
        ffmpegBtn = PillButton(S["FFmpegManualBtn"], () =>
            OpenFileWithExplorerForExtensions(S["FFmpegManualBtn"], null, ApplyManualFFmpegPath, ReqLocateFFmpeg, allowAllFiles: true));
        ffCard.AddView(ffmpegBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

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
        page.AddView(ffCard);

        // 5b. truehdd Dolby TrueHD Engine (Auto-Download & Updater)
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

        truehddProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0,
            Visibility = ViewStates.Gone
        };
        OneUiTheme.StyleProgressBar(this, truehddProgressBar);
        thdCard.AddView(truehddProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)) { BottomMargin = Dp(8) });

        LinearLayout thdBtns = HRow();
        truehddAutoBtn = PrimaryButton(S["TruehddDownloadBtn"], StartTruehddDownloadOrUpdate);
        truehddCheckBtn = PillButton(S["TruehddCheckBtn"], () => _ = CheckTruehddUpdateAsync(silent: false));
        thdBtns.AddView(truehddAutoBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.35f) { RightMargin = Dp(8) });
        thdBtns.AddView(truehddCheckBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        thdCard.AddView(thdBtns);

        AddDivider(thdCard);
        truehddManualBtn = PillButton(S["TruehddManualBtn"], () =>
            OpenFileWithExplorerForExtensions(S["TruehddManualBtn"], null, ApplyManualTruehddPath, ReqLocateTruehdd, allowAllFiles: true));
        thdCard.AddView(truehddManualBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(thdCard);

        // 6. Diagnostics & Shared Logs
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
                Toast.MakeText(this, "Arquivo de log ainda não disponível.", ToastLength.Short).Show();
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
        page.AddView(logCard);

        // 7. Language, About & App Version Updater
        LinearLayout helpCard = Card(S["AboutVerSection"], OneUiIconType.Info, OneUiTheme.IconSystemInfo);
        OneUiInlineSelector langSelector = new(this, S["LanguageLabel"]);
        string[] langLabels = ["Auto (System)", "Português (Brasil)", "English", "Magyar", "简体中文"];
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
            string msg = $"{Listener.Info}\nCavernize Android v{verName} (build {verCode})\n" +
                (OneUiTheme.IsDynamicWallpaperActive ? "Theme: Dynamic One UI Wallpaper Palette\n" : "Theme: Samsung One UI 9 Blue\n") +
                (CavernAmp.Available ? "CavernAmp: Active" : "Managed Pure-C# DSP (ARM64 Ready)");
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
        page.AddView(helpCard);

        RefreshSettingsTabButtons();
        RefreshFFmpegCardUi();
        RefreshTruehddCardUi();
        RefreshSettingsUpdateUi();
        return page;
    }

    void RefreshLogSummaryLabel() {
        if (logPathInfoText != null) {
            long bytes = AppLogger.GetLogSizeBytes();
            logPathInfoText.Text = $"{AppLogger.SharedLogPath}\n({bytes / 1024.0:0.1} KB)";
        }
    }

    void SetFFmpegStatus(string msg, double progress) {
        if (ffmpegStatusText != null) {
            ffmpegStatusText.Text = msg ?? "";
        }
        if (ffmpegProgressBar != null) {
            if (progress < 0) {
                ffmpegProgressBar.Visibility = ViewStates.Visible;
                ffmpegProgressBar.Indeterminate = true;
            } else if (isFFmpegDownloading) {
                ffmpegProgressBar.Visibility = ViewStates.Visible;
                ffmpegProgressBar.Indeterminate = false;
                ffmpegProgressBar.Progress = Math.Clamp((int)Math.Round(progress * 1000.0), 0, 1000);
            } else {
                ffmpegProgressBar.Indeterminate = false;
                ffmpegProgressBar.Visibility = ViewStates.Gone;
            }
        }
    }

    void RefreshFFmpegCardUi() {
        if (ffmpegStatusText == null || ffmpegAutoBtn == null) {
            return;
        }
        bool hasLocal = ffmpeg.Found || !string.IsNullOrEmpty(FFmpegInstallerService.ResolveExistingFFmpegPath(this, store));
        if (latestFFmpegStatus != null) {
            if (latestFFmpegStatus.IsUpdateAvailable) {
                ffmpegStatusText.Text = string.Format(S["FFmpegUpdateAvail"], latestFFmpegStatus.RemoteVersion);
                ffmpegStatusText.SetTextColor(OneUiTheme.FunctionalOrange);
                ffmpegAutoBtn.Text = $"{S["FFmpegUpdateBtn"]} ({latestFFmpegStatus.RemoteVersion})";
                ffmpegAutoBtn.Visibility = ViewStates.Visible;
            } else if (hasLocal) {
                string ver = !string.IsNullOrEmpty(store.InstalledFFmpegVersion)
                    ? store.InstalledFFmpegVersion
                    : latestFFmpegStatus.RemoteVersion;
                ffmpegStatusText.Text = string.Format(S["FFmpegInstalledVer"], string.IsNullOrEmpty(ver) ? "OK" : ver);
                ffmpegStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
                ffmpegAutoBtn.Text = S["FFmpegDownloadBtn"];
                ffmpegAutoBtn.Visibility = ViewStates.Gone;
            } else {
                ffmpegStatusText.Text = S["FFmpegNotInstalled"];
                ffmpegStatusText.SetTextColor(OneUiTheme.TextSub);
                ffmpegAutoBtn.Text = S["FFmpegDownloadBtn"];
                ffmpegAutoBtn.Visibility = ViewStates.Visible;
            }
        } else if (hasLocal) {
            string ver = !string.IsNullOrEmpty(store.InstalledFFmpegVersion)
                ? store.InstalledFFmpegVersion
                : Path.GetFileName(ffmpeg.Location ?? "ffmpeg");
            ffmpegStatusText.Text = string.Format(S["FFmpegInstalledVer"], ver);
            ffmpegStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
            ffmpegAutoBtn.Visibility = ViewStates.Gone;
        } else {
            ffmpegStatusText.Text = S["FFmpegNotInstalled"];
            ffmpegStatusText.SetTextColor(OneUiTheme.TextSub);
            ffmpegAutoBtn.Text = S["FFmpegDownloadBtn"];
            ffmpegAutoBtn.Visibility = ViewStates.Visible;
        }

        if (ffmpegProgressBar != null && !isFFmpegDownloading) {
            ffmpegProgressBar.Indeterminate = false;
            ffmpegProgressBar.Visibility = ViewStates.Gone;
        }
    }

    void SetTruehddStatus(string msg, double progress) {
        if (truehddStatusText != null) {
            truehddStatusText.Text = msg ?? "";
        }
        if (truehddProgressBar != null) {
            if (progress < 0) {
                truehddProgressBar.Visibility = ViewStates.Visible;
                truehddProgressBar.Indeterminate = true;
            } else if (isTruehddDownloading) {
                truehddProgressBar.Visibility = ViewStates.Visible;
                truehddProgressBar.Indeterminate = false;
                truehddProgressBar.Progress = Math.Clamp((int)Math.Round(progress * 1000.0), 0, 1000);
            } else {
                truehddProgressBar.Indeterminate = false;
                truehddProgressBar.Visibility = ViewStates.Gone;
            }
        }
    }

    void RefreshTruehddCardUi() {
        if (truehddStatusText == null || truehddAutoBtn == null) {
            return;
        }
        string localPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        bool hasLocal = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        if (latestTruehddStatus != null) {
            if (latestTruehddStatus.IsUpdateAvailable) {
                truehddStatusText.Text = string.Format(S["TruehddUpdateAvail"], latestTruehddStatus.RemoteVersion);
                truehddStatusText.SetTextColor(OneUiTheme.FunctionalOrange);
                truehddAutoBtn.Text = $"{S["TruehddUpdateBtn"]} (v{latestTruehddStatus.RemoteVersion})";
                truehddAutoBtn.Visibility = ViewStates.Visible;
            } else if (hasLocal) {
                string ver = !string.IsNullOrEmpty(store.InstalledTruehddVersion)
                    ? store.InstalledTruehddVersion
                    : latestTruehddStatus.RemoteVersion;
                truehddStatusText.Text = string.Format(S["TruehddInstalledVer"], string.IsNullOrEmpty(ver) ? "OK" : ver);
                truehddStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
                truehddAutoBtn.Text = S["TruehddDownloadBtn"];
                truehddAutoBtn.Visibility = ViewStates.Gone;
            } else {
                truehddStatusText.Text = S["TruehddNotInstalled"];
                truehddStatusText.SetTextColor(OneUiTheme.TextSub);
                truehddAutoBtn.Text = S["TruehddDownloadBtn"];
                truehddAutoBtn.Visibility = ViewStates.Visible;
            }
        } else if (hasLocal) {
            string ver = !string.IsNullOrEmpty(store.InstalledTruehddVersion)
                ? store.InstalledTruehddVersion
                : Path.GetFileName(localPath);
            truehddStatusText.Text = string.Format(S["TruehddInstalledVer"], ver);
            truehddStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
            truehddAutoBtn.Visibility = ViewStates.Gone;
        } else {
            truehddStatusText.Text = S["TruehddNotInstalled"];
            truehddStatusText.SetTextColor(OneUiTheme.TextSub);
            truehddAutoBtn.Text = S["TruehddDownloadBtn"];
            truehddAutoBtn.Visibility = ViewStates.Visible;
        }

        if (truehddProgressBar != null && !isTruehddDownloading) {
            truehddProgressBar.Indeterminate = false;
            truehddProgressBar.Visibility = ViewStates.Gone;
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

    void RefreshSettingsTabButtons() {
        RefreshBinauralProfileUi();
        if (filtersBtn != null) {
            filtersBtn.Text = renderingSettings.RoomCorrection != null
                ? $"{S["ClearFilters"]} ({Path.GetFileName(store.RoomCorrectionPath)})"
                : S["LoadFilters"];
        }
        if (ffmpegBtn != null) {
            ffmpegBtn.Text = ffmpeg.Found
                ? $"{S["FFmpegManualBtn"]}: {Path.GetFileName(ffmpeg.Location)}"
                : S["FFmpegManualBtn"];
        }
        if (truehddManualBtn != null) {
            string thdPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
            truehddManualBtn.Text = !string.IsNullOrEmpty(thdPath)
                ? $"{S["TruehddManualBtn"]}: {Path.GetFileName(thdPath)}"
                : S["TruehddManualBtn"];
        }
        if (muteBedSwitch != null && muteBedSwitch.Checked != store.MuteBed) {
            muteBedSwitch.Checked = store.MuteBed;
        }
        if (muteGroundSwitch != null && muteGroundSwitch.Checked != store.MuteGround) {
            muteGroundSwitch.Checked = store.MuteGround;
        }
        RefreshLogSummaryLabel();
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
                    playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
                    RefreshBinauralProfileUi();
                    RefreshMusicPlayerControlsUi();
                    ShowToast(string.Format(S["BinauralProfileApplied"], BinauralProfileService.GetActiveDisplayTitle(store, S)));
                } else {
                    ShowError(err ?? "Failed to load HRIR/SOFA file.");
                }
            });
        });
    }

    void ApplyBinauralProfileAsync(string profileId) {
        Task.Run(() => {
            playerEngine?.RequestSmoothTransition();
            bool ok = BinauralProfileService.ApplyProfile(this, store, profileId, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (ok) {
                    playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
                    RefreshBinauralProfileUi();
                    RefreshMusicPlayerControlsUi();
                    ShowToast(string.Format(S["BinauralProfileApplied"], BinauralProfileService.GetActiveDisplayTitle(store, S)));
                } else {
                    ShowError(err ?? "Failed to apply binaural profile.");
                }
            });
        });
    }

    void ApplyMinimumPhaseAsync(bool enabled) {
        Task.Run(() => {
            playerEngine?.RequestSmoothTransition();
            bool ok = BinauralProfileService.SetMinimumPhase(this, store, enabled, out string err);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (ok) {
                    RefreshBinauralProfileUi();
                    RefreshMusicPlayerControlsUi();
                } else {
                    ShowError(err ?? "Failed to update Minimum-Phase mode.");
                }
            });
        });
    }

    void ShowBinauralLicenseCreditsDialog() {
        string credits = BinauralProfileService.ReadLicenseCredits(this);
        ShowMessageDialog(S["BinauralProfileCreditsBtn"], credits, OneUiIconType.Info, monospace: false);
    }

    void ShowBinauralDistanceSelectorDialog() {
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Dialog activeDlg = null;
        string currentId = BinauralProfileService.GetEffectiveProfileId(store);
        BinauralProfileInfo curInfo = BinauralProfileService.FindById(currentId);
        bool isThkSeries = curInfo != null && curInfo.IsThKolnDistanceSeries && store.HeadphoneVirtualizer;

        // Current active profile distance status card
        LinearLayout statusBox = new(this) { Orientation = Orientation.Vertical };
        statusBox.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        statusBox.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
        TextView statusTitle = Label(
            $"{S["BinauralProfileActive"]}: {BinauralProfileService.GetActiveDisplayTitle(store, S)}",
            12.5f, ColText);
        statusTitle.Typeface = OneUiTheme.GetAppleFontBold(this);
        statusTitle.SetSingleLine(true);
        statusTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        string statusSubStr = !store.HeadphoneVirtualizer
            ? S["BinauralDistDirectHint"]
            : (isThkSeries
                ? string.Format(S["BinauralDistSeriesActiveHint"], BinauralProfileService.GetActiveDistanceMeters(store))
                : string.Format(S["BinauralDistFixedHint"], BinauralProfileService.GetActiveDistanceMeters(store)));
        TextView statusSub = Label(statusSubStr, 11.2f, OneUiTheme.TextSecondary);
        statusBox.AddView(statusTitle);
        statusBox.AddView(statusSub, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(3) });
        list.AddView(statusBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) });

        TextView grpHdr = Label(S["BinauralDistSeriesHeader"], 11.5f, OneUiTheme.PeachText);
        grpHdr.Typeface = OneUiTheme.GetAppleFontBold(this);
        grpHdr.SetPadding(Dp(4), 0, Dp(4), Dp(6));
        list.AddView(grpHdr);

        (string id, string descKey)[] distEntries = [
            ("thk_ku100_nf050", "BinauralDistDesc050"),
            ("thk_ku100_nf075", "BinauralDistDesc075"),
            ("thk_ku100_nf100", "BinauralDistDesc100"),
            ("thk_ku100_nf150", "BinauralDistDesc150"),
            ("thk_ku100_ff_dfc", "BinauralDistDesc325")
        ];

        for (int i = 0; i < distEntries.Length; i++) {
            string pid = distEntries[i].id;
            string dKey = distEntries[i].descKey;
            BinauralProfileInfo p = BinauralProfileService.FindById(pid);
            if (p == null) continue;
            bool isSel = store.HeadphoneVirtualizer && string.Equals(currentId, pid, StringComparison.OrdinalIgnoreCase);

            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = isSel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 16f, OneUiTheme.PrimaryHigh, 1f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
            OneUiTheme.AttachRecoil(row, 0.98f);

            TextView badgeTv = Label($"{p.ReferenceDistanceMeters:0.00} m", 12.5f, isSel ? Color.White : OneUiTheme.PeachText);
            badgeTv.SetIncludeFontPadding(false);
            badgeTv.Typeface = OneUiTheme.GetAppleFontBold(this);
            badgeTv.Gravity = GravityFlags.Center;
            badgeTv.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
            badgeTv.Background = OneUiTheme.CreateRoundedDrawable(
                this,
                isSel ? OneUiTheme.Primary : OneUiTheme.WithAlpha(OneUiTheme.IconSliderMedia, 36),
                12f);
            row.AddView(badgeTv, new LinearLayout.LayoutParams(Dp(68), ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(10) });

            LinearLayout col = new(this) { Orientation = Orientation.Vertical };
            TextView titleTv = Label(S[dKey], 12.8f, isSel ? ColText : OneUiTheme.TextPrimary);
            titleTv.SetIncludeFontPadding(false);
            titleTv.Typeface = isSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
            titleTv.SetSingleLine(true);
            titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            TextView subTv = Label(S[p.SubtitleKey], 10.8f, isSel ? OneUiTheme.TextSecondary : OneUiTheme.TextMuted);
            subTv.SetSingleLine(true);
            subTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            col.AddView(titleTv);
            col.AddView(subTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });

            row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
            if (isSel) {
                row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
            }

            row.Click += (_, _) => {
                activeDlg?.Dismiss();
                ApplyBinauralProfileAsync(pid);
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }

        Button allProfilesBtn = PillButton(S["BinauralDistAllProfilesBtn"], () => {
            activeDlg?.Dismiss();
            ShowBinauralProfileSelectorDialog();
        });
        list.AddView(allProfilesBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

        activeDlg = OneUiModalDialog.Show(
            this,
            S["BinauralDistModalTitle"],
            customContent: list,
            icon: OneUiIconType.Distance,
            iconBadgeColor: OneUiTheme.IconSliderMedia,
            positiveText: S["CloseBtn"]);
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
            ShowError("Unsupported files:\n" + string.Join('\n', invalids));
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
            queueListContainer.AddView(Label("0 jobs", 12f, ColMuted));
            return;
        }
        foreach (QueuedJob job in renderQueue.Jobs) {
            queueListContainer.AddView(Label($"• {job}", 12f, ColText));
        }
    }
}
