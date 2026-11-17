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
    Button hrirBtn, filtersBtn;
    Switch muteBedSwitch, muteGroundSwitch, speakerVirtSwitch, force24Switch, surroundSwapSwitch, wavSkipSwitch, reportModeSwitch, gradingSwitch;
    Switch pedanticSwitch, unsafeSwitch, keepTempSwitch, normSwitch, limiterOnlySwitch;
    OneUiSeekBar upmixEffectBar, upmixSmoothBar;
    Switch matrixUpmixSwitch, cavernizeUpmixSwitch;

    LinearLayout BuildSettingsTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(2), Dp(14), Dp(88));

        // 1. Most Frequently Adjusted: Spatial 3D Upmixing
        LinearLayout upmixCard = Card(S["UpmixTitle"], OneUiIconType.Sparkles, OneUiTheme.FunctionalIndigo, S["HelpUpmix"]);
        matrixUpmixSwitch = AddSwitchRow(upmixCard, S["MatrixUpmix"], store.MatrixUpmixing, v => {
            store.MatrixUpmixing = v;
            ReattachPlayerTrack();
        });
        AddDivider(upmixCard);
        cavernizeUpmixSwitch = AddSwitchRow(upmixCard, S["CavernizeUpmix"], store.CavernizeUpmix, v => {
            store.CavernizeUpmix = v;
            ReattachPlayerTrack();
        });
        AddDivider(upmixCard);
        (upmixEffectBar, _) = AddSliderRow(upmixCard, S["UpmixEffect"], 0, 100, (int)Math.Round(store.UpmixEffect * 100), v => {
            store.UpmixEffect = v * 0.01f;
            ReattachPlayerTrack();
        }, "%", defaultValue: 75);
        (upmixSmoothBar, _) = AddSliderRow(upmixCard, S["UpmixSmooth"], 0, 100, (int)Math.Round(store.UpmixSmoothness * 100), v => {
            store.UpmixSmoothness = v * 0.01f;
            ReattachPlayerTrack();
        }, "%", defaultValue: 80);
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
        });
        upmixCard.AddView(resetUpmixBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        page.AddView(upmixCard);

        // 2. Room Acoustics, Gain & Dynamic Limiter
        LinearLayout acousticsCard = Card(S["RoomAcousticsTitle"], OneUiIconType.Sliders, OneUiTheme.Primary, S["HelpRoomAcoustics"]);
        normSwitch = AddSwitchRow(acousticsCard, S["NormalizerCheck"], store.NormalizerEnabled, v => {
            store.NormalizerEnabled = v;
            playerEngine.NormalizerEnabled = v;
        });
        AddDivider(acousticsCard);
        limiterOnlySwitch = AddSwitchRow(acousticsCard, S["LimiterOnlyCheck"], store.LimiterOnly, v => {
            store.LimiterOnly = v;
            playerEngine.LimiterOnly = v;
        });
        AddDivider(acousticsCard);
        AddSliderRow(acousticsCard, S["RenderGain"], -24, 24, (int)Math.Round(store.RenderGainDb), v => {
            store.RenderGainDb = v;
            store.ApplyGlobals();
        }, " dB", defaultValue: 0);
        AddSliderRow(acousticsCard, S["HeightSqueeze"], 0, 100, (int)Math.Round(store.HeightSqueezePercent), v => {
            store.HeightSqueezePercent = v;
            store.ApplyGlobals();
        }, "%", defaultValue: 0);

        OneUiInlineSelector envSelector = new(this, S["EnvType"]);
        string[] envNames = Enum.GetNames(typeof(Environments));
        envSelector.SetItems(envNames, Math.Clamp(store.EnvironmentTypeIndex, 0, envNames.Length - 1));
        envSelector.SelectionChanged += pos => {
            store.EnvironmentTypeIndex = pos;
            store.ApplyGlobals();
            playerEngine.EnvironmentType = (Environments)pos;
            UpdateRoomCanvas();
        };
        acousticsCard.AddView(envSelector);

        AddDivider(acousticsCard);
        acousticsCard.AddView(Label(S["RoomSize"], 12f, ColMuted));
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

        // 3. Headphone HRIR & QuickEQ Calibration
        LinearLayout rendCard = Card(S["CalibTitle"], OneUiIconType.Headphones, OneUiTheme.FunctionalGreen, S["HelpCalib"]);
        hrirBtn = PillButton(S["LoadHRIR"], () => {
            if (!string.IsNullOrEmpty(store.HrirPath)) {
                VirtualizerFilter.Reset();
                store.HrirPath = "";
                RefreshSettingsTabButtons();
            } else {
                LaunchFilePicker(ReqLoadHrir, false);
            }
        });
        rendCard.AddView(hrirBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        filtersBtn = PillButton(S["LoadFilters"], () => {
            if (renderingSettings.RoomCorrection != null) {
                renderingSettings.RoomCorrection = null;
                store.RoomCorrectionPath = "";
                RefreshSettingsTabButtons();
            } else {
                LaunchFilePicker(ReqLoadFilters, false);
            }
        });
        rendCard.AddView(filtersBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        speakerVirtSwitch = AddSwitchRow(rendCard, S["SpeakerVirt"], store.SpeakerVirtualizer, v => store.SpeakerVirtualizer = v);
        page.AddView(rendCard);

        // 4. Collapsible Advanced Rendering & CLI Options
        LinearLayout advCard = Card();
        LinearLayout advHeader = HRow();
        OneUiTheme.AttachRecoil(advHeader, 0.98f);
        advHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Wrench, OneUiTheme.FunctionalAmber, 32));
        TextView advTitleTv = SectionTitle(S["AdvancedTitle"]);
        advTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        advHeader.AddView(advTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        advChevron = new OneUiIconView(this, isAdvancedExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown, OneUiTheme.PeachText);
        advHeader.AddView(advChevron, new LinearLayout.LayoutParams(Dp(18), Dp(18)));
        advCard.AddView(advHeader);
        AttachCardHelp(advCard, advHeader, S["HelpAdvanced"]);

        advContentContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isAdvancedExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        advContentContainer.SetPadding(0, Dp(12), 0, 0);
        advHeader.Click += (_, _) => {
            isAdvancedExpanded = !isAdvancedExpanded;
            advContentContainer.Visibility = isAdvancedExpanded ? ViewStates.Visible : ViewStates.Gone;
            advChevron.IconType = isAdvancedExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
        };

        muteBedSwitch = AddSwitchRow(advContentContainer, S["MuteBed"], store.MuteBed, v => {
            store.MuteBed = v;
            playerEngine.MuteBed = v;
            refreshBentoTilesUi?.Invoke();
        });
        AddDivider(advContentContainer);
        muteGroundSwitch = AddSwitchRow(advContentContainer, S["MuteGround"], store.MuteGround, v => {
            store.MuteGround = v;
            playerEngine.MuteGround = v;
            refreshBentoTilesUi?.Invoke();
        });
        AddDivider(advContentContainer);
        force24Switch = AddSwitchRow(advContentContainer, S["Force24Bit"], store.Force24Bit, v => store.Force24Bit = v);
        AddDivider(advContentContainer);
        surroundSwapSwitch = AddSwitchRow(advContentContainer, S["SurroundSwap"], store.SurroundSwap, v => store.SurroundSwap = v);
        AddDivider(advContentContainer);
        wavSkipSwitch = AddSwitchRow(advContentContainer, S["WavSkip"], store.WavChannelSkip, v => store.WavChannelSkip = v);
        AddDivider(advContentContainer);
        reportModeSwitch = AddSwitchRow(advContentContainer, S["ReportMode"], store.ReportOnlyMode, v => store.ReportOnlyMode = v);
        AddDivider(advContentContainer);
        gradingSwitch = AddSwitchRow(advContentContainer, S["QualityGrading"], store.QualityGrading, v => store.QualityGrading = v);
        AddDivider(advContentContainer);

        AddSliderRow(advContentContainer, S["MlpPres"], -1, 4, store.MlpPresentation, v => {
            store.MlpPresentation = v;
            store.ApplyGlobals();
        }, "", useBlue: true, defaultValue: -1);

        OneUiInlineSelector qualSelector = new(this, S["AudioQuality"]);
        string[] qualNames = Enum.GetNames(typeof(QualityModes));
        qualSelector.SetItems(qualNames, Math.Clamp(store.AudioQualityIndex, 0, qualNames.Length - 1));
        qualSelector.SelectionChanged += pos => {
            store.AudioQualityIndex = pos;
            playerEngine.AudioQuality = (QualityModes)pos;
        };
        advContentContainer.AddView(qualSelector);

        AddDivider(advContentContainer);
        pedanticSwitch = AddSwitchRow(advContentContainer, S["Pedantic"], store.Pedantic, v => { store.Pedantic = v; store.ApplyGlobals(); });
        AddDivider(advContentContainer);
        unsafeSwitch = AddSwitchRow(advContentContainer, S["Unsafe"], store.Unsafe, v => { store.Unsafe = v; store.ApplyGlobals(); });
        AddDivider(advContentContainer);
        keepTempSwitch = AddSwitchRow(advContentContainer, S["KeepTemp"], store.KeepTempFiles, v => { store.KeepTempFiles = v; store.ApplyGlobals(); });

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) {
            Button permBtn = PillButton(S["StoragePermBtn"], RequestAllFilesAccess);
            advContentContainer.AddView(permBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        }

        advCard.AddView(advContentContainer);
        page.AddView(advCard);

        // 5. One-Time Setup: FFmpeg Audio Engine (Auto-Download & Updater)
        LinearLayout ffCard = Card(S["FFmpegCardTitle"], OneUiIconType.Gear, OneUiTheme.CoralStart, S["HelpFFmpeg"]);
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
        ffmpegBtn = PillButton(S["FFmpegManualBtn"], () => LaunchFilePicker(ReqLocateFFmpeg, false));
        ffCard.AddView(ffmpegBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        ffCard.AddView(Label(S["FFmpegArgsLabel"], 12f, ColMuted));
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

        // 6. Diagnostics & Shared Logs
        LinearLayout logCard = Card(S["DiagnosticsTitle"], OneUiIconType.Logs, OneUiTheme.FunctionalCyan, S["HelpDiagnostics"]);
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
        LinearLayout helpCard = Card(S["AboutVerSection"], OneUiIconType.Info, OneUiTheme.Primary, S["HelpAbout"]);
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

        updateSettingsStatusText = Label(S["Ready"], 12f, ColCyan);
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
        if (selectedTrack != null && selectedTrack.Supported && selectedTrack.Codec != Codec.TrueHD) {
            bool wasPlaying = playerEngine.IsPlaying;
            double sec = playerEngine.CurrentSeconds;
            playerEngine.Load(selectedTrack, upmixingSettings, renderingSettings);
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
        if (hrirBtn != null) {
            hrirBtn.Text = !string.IsNullOrEmpty(store.HrirPath)
                ? $"{S["ResetHRIR"]} ({Path.GetFileName(store.HrirPath)})"
                : S["LoadHRIR"];
        }
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
        if (muteBedSwitch != null && muteBedSwitch.Checked != store.MuteBed) {
            muteBedSwitch.Checked = store.MuteBed;
        }
        if (muteGroundSwitch != null && muteGroundSwitch.Checked != store.MuteGround) {
            muteGroundSwitch.Checked = store.MuteGround;
        }
        RefreshLogSummaryLabel();
    }

    bool TryLoadHrir(string path, bool showError) {
        try {
            using RIFFWaveReader reader = new(path);
            reader.ReadHeader();
            VirtualizerFilter.Override(VirtualChannel.Parse(new MultichannelWaveform(reader.ReadMultichannelAfterHeader()), reader.SampleRate),
                reader.SampleRate);
            AppLogger.Info($"Loaded custom HRIR: '{path}'");
            return true;
        } catch (Exception ex) {
            AppLogger.Error("TryLoadHrir", ex);
            if (showError) {
                ShowError(ex.Message);
            }
            return false;
        }
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
            RefreshQueueList();
        }
    }

    void AddUrisToQueueAsync(List<Android.Net.Uri> uris) {
        string targetFolder = AndroidFileHelper.GetWorkingOutputDir(this, "Queue");
        Task.Run(() => {
            StringCollection paths = [];
            foreach (Android.Net.Uri uri in uris) {
                try {
                    paths.Add(AndroidFileHelper.ResolveReadablePath(this, uri));
                } catch {
                    // Skip unreadable
                }
            }
            RunOnUiThread(() => {
                List<string> invalids = [];
                renderQueue.AddRange(paths, targetFolder, invalids, ffmpeg);
                RefreshQueueList();
                if (invalids.Count > 0) {
                    ShowError("Unsupported files:\n" + string.Join('\n', invalids));
                }
            });
        });
    }

    void StartQueueProcessing() {
        if (isOperationRunning || renderQueue.Jobs.Count == 0) {
            return;
        }
        StopPlayback();
        QueuedJob[] jobs = [.. renderQueue.Jobs];
        RunBackgroundOperation(token => {
            for (int i = 0; i < jobs.Length; i++) {
                token.ThrowIfCancellationRequested();
                QueuedJob job = jobs[i];
                RunOnUiThreadSync(() => {
                    job.Prepare(this);
                    AttachToListener();
                });
                job.Run();
                RunOnUiThreadSync(() => {
                    renderQueue.Jobs.Remove(job);
                    RefreshQueueList();
                });
            }
            RunOnUiThreadSync(() => Reset());
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
