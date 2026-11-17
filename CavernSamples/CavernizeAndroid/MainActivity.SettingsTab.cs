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

using VoidX.WPF.FFmpeg;
using VoidX.WPF.Language;

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

    LinearLayout queueListContainer;
    Button hrirBtn, filtersBtn;
    Switch muteBedSwitch, muteGroundSwitch, speakerVirtSwitch, force24Switch, surroundSwapSwitch, wavSkipSwitch, reportModeSwitch, gradingSwitch;
    Switch pedanticSwitch, unsafeSwitch, keepTempSwitch, normSwitch, limiterOnlySwitch;
    SeekBar upmixEffectBar, upmixSmoothBar;
    Switch matrixUpmixSwitch, cavernizeUpmixSwitch;

    LinearLayout BuildSettingsTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(4), Dp(14), Dp(88));

        // 1. FFmpeg Audio Engine (1-Click Download & Auto-Updater)
        LinearLayout ffCard = Card(S["FFmpegCardTitle"], "⚙", OneUiTheme.CoralStart);
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

        LinearLayout ffBtns = new(this) { Orientation = Orientation.Horizontal };
        ffmpegAutoBtn = PrimaryButton(S["FFmpegDownloadBtn"], StartFFmpegDownloadOrUpdate);
        ffmpegCheckBtn = PillButton(S["FFmpegCheckBtn"], () => _ = CheckFFmpegUpdateAsync(silent: false));
        ffBtns.AddView(ffmpegAutoBtn, new LinearLayout.LayoutParams(0, Dp(42), 1.35f) { RightMargin = Dp(8) });
        ffBtns.AddView(ffmpegCheckBtn, new LinearLayout.LayoutParams(0, Dp(42), 1f));
        ffCard.AddView(ffBtns);

        AddDivider(ffCard);
        ffmpegBtn = PillButton(S["FFmpegManualBtn"], () => LaunchFilePicker(ReqLocateFFmpeg, false));
        ffCard.AddView(ffmpegBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(38)) { BottomMargin = Dp(8) });

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

        // 2. Render Queue Card
        LinearLayout queueCard = Card(S["QueueTitle"], "☰", OneUiTheme.Primary);
        queueListContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        queueCard.AddView(queueListContainer);

        LinearLayout qBtns = new(this) { Orientation = Orientation.Horizontal };
        qBtns.SetPadding(0, Dp(8), 0, 0);
        Button batchBtn = PillButton(S["BatchAddBtn"], () => LaunchFilePicker(ReqBatchMedia, allowMultiple: true));
        Button remBtn = PillButton(S["RemoveQueueBtn"], RemoveLastFromQueue);
        Button runQBtn = PrimaryButton(S["ProcessQueueBtn"], StartQueueProcessing);
        qBtns.AddView(batchBtn, new LinearLayout.LayoutParams(0, Dp(40), 1f) { RightMargin = Dp(6) });
        qBtns.AddView(remBtn, new LinearLayout.LayoutParams(0, Dp(40), 0.8f) { RightMargin = Dp(6) });
        qBtns.AddView(runQBtn, new LinearLayout.LayoutParams(0, Dp(40), 1.2f));
        queueCard.AddView(qBtns);
        page.AddView(queueCard);

        // 3. Upmixing Setup Card
        LinearLayout upmixCard = Card(S["UpmixTitle"], "✦", OneUiTheme.FunctionalIndigo);
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
        }, "%");
        (upmixSmoothBar, _) = AddSliderRow(upmixCard, S["UpmixSmooth"], 0, 100, (int)Math.Round(store.UpmixSmoothness * 100), v => {
            store.UpmixSmoothness = v * 0.01f;
            ReattachPlayerTrack();
        }, "%");
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
        upmixCard.AddView(resetUpmixBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(38)) { TopMargin = Dp(4) });
        page.AddView(upmixCard);

        // 4. Calibration, Rendering & Filters Card
        LinearLayout rendCard = Card(S["CalibTitle"], "🎧", OneUiTheme.FunctionalGreen);
        hrirBtn = PillButton(S["LoadHRIR"], () => {
            if (!string.IsNullOrEmpty(store.HrirPath)) {
                VirtualizerFilter.Reset();
                store.HrirPath = "";
                RefreshSettingsTabButtons();
            } else {
                LaunchFilePicker(ReqLoadHrir, false);
            }
        });
        rendCard.AddView(hrirBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(40)) { BottomMargin = Dp(8) });

        filtersBtn = PillButton(S["LoadFilters"], () => {
            if (renderingSettings.RoomCorrection != null) {
                renderingSettings.RoomCorrection = null;
                store.RoomCorrectionPath = "";
                RefreshSettingsTabButtons();
            } else {
                LaunchFilePicker(ReqLoadFilters, false);
            }
        });
        rendCard.AddView(filtersBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(40)) { BottomMargin = Dp(8) });

        speakerVirtSwitch = AddSwitchRow(rendCard, S["SpeakerVirt"], store.SpeakerVirtualizer, v => store.SpeakerVirtualizer = v);
        AddDivider(rendCard);
        muteBedSwitch = AddSwitchRow(rendCard, S["MuteBed"], store.MuteBed, v => {
            store.MuteBed = v;
            playerEngine.MuteBed = v;
            refreshBentoTilesUi?.Invoke();
        });
        AddDivider(rendCard);
        muteGroundSwitch = AddSwitchRow(rendCard, S["MuteGround"], store.MuteGround, v => {
            store.MuteGround = v;
            playerEngine.MuteGround = v;
            refreshBentoTilesUi?.Invoke();
        });
        AddDivider(rendCard);
        force24Switch = AddSwitchRow(rendCard, S["Force24Bit"], store.Force24Bit, v => store.Force24Bit = v);
        AddDivider(rendCard);
        surroundSwapSwitch = AddSwitchRow(rendCard, S["SurroundSwap"], store.SurroundSwap, v => store.SurroundSwap = v);
        AddDivider(rendCard);
        wavSkipSwitch = AddSwitchRow(rendCard, S["WavSkip"], store.WavChannelSkip, v => store.WavChannelSkip = v);
        AddDivider(rendCard);
        reportModeSwitch = AddSwitchRow(rendCard, S["ReportMode"], store.ReportOnlyMode, v => store.ReportOnlyMode = v);
        AddDivider(rendCard);
        gradingSwitch = AddSwitchRow(rendCard, S["QualityGrading"], store.QualityGrading, v => store.QualityGrading = v);
        page.AddView(rendCard);

        // 5. Advanced & Room Acoustics CLI Settings Card
        LinearLayout advCard = Card(S["AdvancedTitle"], "🔧", OneUiTheme.FunctionalAmber);

        AddSliderRow(advCard, S["RenderGain"], -24, 24, (int)Math.Round(store.RenderGainDb), v => {
            store.RenderGainDb = v;
            store.ApplyGlobals();
        }, " dB");
        AddSliderRow(advCard, S["HeightSqueeze"], 0, 100, (int)Math.Round(store.HeightSqueezePercent), v => {
            store.HeightSqueezePercent = v;
            store.ApplyGlobals();
        }, "%");
        AddSliderRow(advCard, S["MlpPres"], -1, 4, store.MlpPresentation, v => {
            store.MlpPresentation = v;
            store.ApplyGlobals();
        }, "", useBlue: true);

        advCard.AddView(Label(S["EnvType"], 12f, ColMuted));
        Spinner envSpinner = new(this);
        string[] envNames = Enum.GetNames(typeof(Environments));
        ArrayAdapter<string> envAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, envNames);
        envAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        envSpinner.Adapter = envAdapter;
        envSpinner.SetSelection(Math.Clamp(store.EnvironmentTypeIndex, 0, envNames.Length - 1));
        envSpinner.ItemSelected += (_, e) => {
            store.EnvironmentTypeIndex = e.Position;
            store.ApplyGlobals();
            playerEngine.EnvironmentType = (Environments)e.Position;
            UpdateRoomCanvas();
        };
        advCard.AddView(envSpinner);

        AddDivider(advCard);
        advCard.AddView(Label(S["RoomSize"], 12f, ColMuted));
        LinearLayout roomDimRow = new(this) { Orientation = Orientation.Horizontal };
        roomDimRow.SetPadding(0, Dp(4), 0, Dp(4));
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
        roomDimRow.AddView(applyRoomBtn, new LinearLayout.LayoutParams(Dp(56), Dp(40)));
        advCard.AddView(roomDimRow);

        AddDivider(advCard);
        advCard.AddView(Label(S["AudioQuality"], 12f, ColMuted));
        Spinner qualSpinner = new(this);
        string[] qualNames = Enum.GetNames(typeof(QualityModes));
        ArrayAdapter<string> qualAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, qualNames);
        qualAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        qualSpinner.Adapter = qualAdapter;
        qualSpinner.SetSelection(Math.Clamp(store.AudioQualityIndex, 0, qualNames.Length - 1));
        qualSpinner.ItemSelected += (_, e) => {
            store.AudioQualityIndex = e.Position;
            playerEngine.AudioQuality = (QualityModes)e.Position;
        };
        advCard.AddView(qualSpinner);

        AddDivider(advCard);
        pedanticSwitch = AddSwitchRow(advCard, S["Pedantic"], store.Pedantic, v => { store.Pedantic = v; store.ApplyGlobals(); });
        AddDivider(advCard);
        unsafeSwitch = AddSwitchRow(advCard, S["Unsafe"], store.Unsafe, v => { store.Unsafe = v; store.ApplyGlobals(); });
        AddDivider(advCard);
        keepTempSwitch = AddSwitchRow(advCard, S["KeepTemp"], store.KeepTempFiles, v => { store.KeepTempFiles = v; store.ApplyGlobals(); });
        AddDivider(advCard);
        normSwitch = AddSwitchRow(advCard, S["NormalizerCheck"], store.NormalizerEnabled, v => { store.NormalizerEnabled = v; playerEngine.NormalizerEnabled = v; });
        AddDivider(advCard);
        limiterOnlySwitch = AddSwitchRow(advCard, S["LimiterOnlyCheck"], store.LimiterOnly, v => { store.LimiterOnly = v; playerEngine.LimiterOnly = v; });

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) {
            Button permBtn = PillButton(S["StoragePermBtn"], RequestAllFilesAccess);
            advCard.AddView(permBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(40)) { TopMargin = Dp(8) });
        }

        page.AddView(advCard);

        // 6. Language, About & App Version Updater Card (discreetly kept in About section only)
        LinearLayout helpCard = Card(S["AboutVerSection"], "ℹ", OneUiTheme.FunctionalCyan);
        helpCard.AddView(Label(S["LanguageLabel"], 12f, ColMuted));
        Spinner langSpinner = new(this);
        string[] langLabels = ["Auto (System)", "Português (Brasil)", "English", "Magyar", "简体中文"];
        string[] langCodes = ["", "pt-BR", "en-US", "hu-HU", "zh-CN"];
        ArrayAdapter<string> langAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, langLabels);
        langAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        langSpinner.Adapter = langAdapter;
        int curLangIdx = Array.IndexOf(langCodes, store.LanguageCode ?? "");
        langSpinner.SetSelection(Math.Max(0, curLangIdx));
        langSpinner.ItemSelected += (_, e) => {
            string chosen = langCodes[e.Position];
            if (!string.Equals(store.LanguageCode ?? "", chosen, StringComparison.Ordinal)) {
                ApplyLanguage(chosen);
            }
        };
        helpCard.AddView(langSpinner);

        LinearLayout helpBtns = new(this) { Orientation = Orientation.Horizontal };
        helpBtns.SetPadding(0, Dp(10), 0, Dp(4));
        Button guideBtn = PillButton(S["UserGuideBtn"], () => {
            Intent browser = new(Intent.ActionView, Android.Net.Uri.Parse("https://cavern.sbence.hu/cavern/doc.php?p=Cavernize"));
            StartActivity(browser);
        });
        Button aboutBtn = PillButton(S["AboutBtn"], () => {
            (string verName, long verCode) = AppUpdateService.GetInstalledVersion(this);
            string msg = $"{Listener.Info}\nCavernize Android v{verName} (build {verCode})\n" +
                (CavernAmp.Available ? "CavernAmp: Active" : "Managed Pure-C# DSP (ARM64 Ready)");
            ShowMessageDialog(S["AboutBtn"], msg);
        });
        helpBtns.AddView(guideBtn, new LinearLayout.LayoutParams(0, Dp(40), 1f) { RightMargin = Dp(8) });
        helpBtns.AddView(aboutBtn, new LinearLayout.LayoutParams(0, Dp(40), 1f));
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

        LinearLayout updBtns = new(this) { Orientation = Orientation.Horizontal };
        checkUpdatesBtn = PillButton(S["CheckUpdateBtn"], () => _ = CheckForUpdatesAsync(silent: false));
        downloadInstallBtn = PrimaryButton(S["DownloadInstallBtn"], StartUpdateDownloadAndInstall);
        downloadInstallBtn.Visibility = ViewStates.Gone;
        updBtns.AddView(checkUpdatesBtn, new LinearLayout.LayoutParams(0, Dp(40), 1f));
        updBtns.AddView(downloadInstallBtn, new LinearLayout.LayoutParams(0, Dp(40), 1.2f) { LeftMargin = Dp(8) });
        helpCard.AddView(updBtns);

        AddDivider(helpCard);
        autoCheckUpdatesSwitch = AddSwitchRow(helpCard, S["AutoCheckSwitch"], store.AutoCheckUpdates, v => store.AutoCheckUpdates = v);
        page.AddView(helpCard);

        RefreshQueueList();
        RefreshSettingsTabButtons();
        RefreshFFmpegCardUi();
        RefreshSettingsUpdateUi();
        return page;
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
                : System.IO.Path.GetFileName(ffmpeg.Location ?? "ffmpeg");
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
                ? $"{S["ResetHRIR"]} ({System.IO.Path.GetFileName(store.HrirPath)})"
                : S["LoadHRIR"];
        }
        if (filtersBtn != null) {
            filtersBtn.Text = renderingSettings.RoomCorrection != null
                ? $"{S["ClearFilters"]} ({System.IO.Path.GetFileName(store.RoomCorrectionPath)})"
                : S["LoadFilters"];
        }
        if (ffmpegBtn != null) {
            ffmpegBtn.Text = ffmpeg.Found
                ? $"{S["FFmpegManualBtn"]}: {System.IO.Path.GetFileName(ffmpeg.Location)}"
                : S["FFmpegManualBtn"];
        }
        if (muteBedSwitch != null && muteBedSwitch.Checked != store.MuteBed) {
            muteBedSwitch.Checked = store.MuteBed;
        }
        if (muteGroundSwitch != null && muteGroundSwitch.Checked != store.MuteGround) {
            muteGroundSwitch.Checked = store.MuteGround;
        }
    }

    bool TryLoadHrir(string path, bool showError) {
        try {
            using RIFFWaveReader reader = new(path);
            reader.ReadHeader();
            VirtualizerFilter.Override(VirtualChannel.Parse(new MultichannelWaveform(reader.ReadMultichannelAfterHeader()), reader.SampleRate),
                reader.SampleRate);
            return true;
        } catch (Exception ex) {
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
        } catch (Exception ex) {
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
