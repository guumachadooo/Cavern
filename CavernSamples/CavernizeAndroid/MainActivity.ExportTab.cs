using Cavernize.Logic.Rendering;
using Cavernize.Logic.Models.RenderTargets;
using System.IO;
using System.Threading;

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    LinearLayout exportSourceBarHost;
    LinearLayout exportExtractSection;
    LinearLayout exportUnifiedQueueCard;
    LinearLayout exportFooterCard;
    Button exportProcessQueueBtn;
    TextView exportQueueBadgeTv;
    readonly List<Button> exportSubModeBtns = [];
    int selectedExportSubMode; // 0 = Master, 1 = Convert, 2 = Extract

    /// <summary>
    /// Unified Export Workbench (W55.4.3): merges legacy Studio (Master + Convert) and Stems (Extract)
    /// under a single Export tab with a local Source File bar, 3-way Master | Convert | Extract switcher,
    /// and a shared unified Batch Queue card.
    /// </summary>
    LinearLayout BuildUnifiedExportTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        ApplyTabPageCutoutPadding(page);

        // 1. Local Source File Bar (replaces removed global workspaceChromeHost)
        exportSourceBarHost = new LinearLayout(this) { Orientation = Orientation.Vertical };
        exportSourceBarHost.SetPadding(0, 0, 0, Dp(10));
        exportSourceBarHost.AddView(
            CreateWorkspaceSourceBar(),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(exportSourceBarHost);

        // 2. Top-level One UI 9 Segmented Switcher (Master | Convert | Extract) + Conditional Readiness Card
        LinearLayout modeCard = Card();
        string[] subModeNames = [
            S["ExportSubModeMaster"],
            S["ExportSubModeConvert"],
            S["ExportSubModeExtract"]
        ];

        LinearLayout segCapsule = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        segCapsule.SetGravity(GravityFlags.CenterVertical);
        segCapsule.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp, OneUiTheme.BorderSubtle, 1f);
        segCapsule.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));

        exportSubModeBtns.Clear();
        studioWorkflowPillBtns.Clear();
        for (int i = 0; i < subModeNames.Length; i++) {
            int capturedIdx = i;
            Button pill = PillButton(subModeNames[i], () => SelectExportSubMode(capturedIdx), active: i == selectedExportSubMode);
            pill.TextSize = 11.8f;
            pill.SetSingleLine(true);
            pill.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            pill.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
            exportSubModeBtns.Add(pill);
            studioWorkflowPillBtns.Add(pill);
            segCapsule.AddView(pill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                RightMargin = i < subModeNames.Length - 1 ? Dp(4) : 0
            });
        }
        modeCard.AddView(segCapsule);

        studioReadinessContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        AddDivider(studioReadinessContainer);
        TextView readyHdr = Label(S["StudioReadinessHeader"], 11.5f, ColMuted);
        readyHdr.SetPadding(0, Dp(2), 0, Dp(6));
        studioReadinessContainer.AddView(readyHdr);

        studioReadinessBadgeRow1 = HRow();
        studioBadgeTruehdBtn = PillButton(S["StudioBadgeTrueHdReady"], OnTrueHdReadinessBadgeClicked);
        studioBadgeFfmpegBtn = PillButton(S["StudioBadgeFFmpegReady"], () => ShowAppSettingsSheet());
        studioBadgeTruehdBtn.TextSize = 11f;
        studioBadgeFfmpegBtn.TextSize = 11f;
        studioReadinessBadgeRow1.AddView(studioBadgeTruehdBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        studioReadinessBadgeRow1.AddView(studioBadgeFfmpegBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        studioReadinessContainer.AddView(studioReadinessBadgeRow1, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        studioReadinessBadgeRow2 = HRow();
        studioBadgeOnnxBtn = PillButton(S["StudioBadgeOnnxReady"], OnOnnxReadinessBadgeClicked);
        studioBadgeTwinBtn = PillButton(S["StudioBadgeTwinNone"], OnTwinReadinessBadgeClicked);
        studioBadgeOnnxBtn.TextSize = 11f;
        studioBadgeTwinBtn.TextSize = 11f;
        studioReadinessBadgeRow2.AddView(studioBadgeOnnxBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        studioReadinessBadgeRow2.AddView(studioBadgeTwinBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        studioReadinessContainer.AddView(studioReadinessBadgeRow2);
        modeCard.AddView(studioReadinessContainer);

        studioPrereqJumpBtn = SecondaryButton(S["OpenSettingsBtn"], OnStudioPrerequisiteJumpClicked);
        studioPrereqJumpBtn.Visibility = ViewStates.Gone;
        modeCard.AddView(studioPrereqJumpBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });
        page.AddView(modeCard);

        // 3. Sub-Mode 0: Master (Studio Spatial Mastering & Vocal Performance Recorder)
        studioMasterSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        studioMasterSection.AddView(BuildStudioPerformanceRecorderCard());
        studioMasterSection.AddView(BuildStudioComposableExportCard());
        page.AddView(studioMasterSection);

        // 4. Sub-Mode 1: Convert (System Target + Codec/Container + Pro Options)
        studioClassicSection = BuildClassicConvertSection();
        page.AddView(studioClassicSection);

        // 5. Sub-Mode 2: Extract (Stem & AI Object Separation)
        exportExtractSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        exportExtractSection.AddView(BuildExtractorTabContent());
        page.AddView(exportExtractSection);

        // 6. Primary Action, 4-Stage Pipeline Progress, Cancel, Locate/Share/Play Footer (Master & Convert)
        exportFooterCard = BuildStudioFooterCard();
        page.AddView(exportFooterCard);

        // 7. Unified Shared Batch Queue Card (accessible across Master | Convert | Extract)
        studioQueueSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Visible
        };
        exportUnifiedQueueCard = Card(S["QueueTitle"], OneUiIconType.Queue, OneUiTheme.IconAtmosUpmix);
        LinearLayout qMetaRow = HRow();
        qMetaRow.SetPadding(0, 0, 0, Dp(6));
        exportQueueBadgeTv = Label(S["QueueEmpty"], 11.5f, OneUiTheme.TextSub);
        qMetaRow.AddView(exportQueueBadgeTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        exportUnifiedQueueCard.AddView(qMetaRow);

        queueListContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        exportUnifiedQueueCard.AddView(queueListContainer);

        LinearLayout qRow1 = HRow();
        qRow1.SetPadding(0, Dp(10), 0, Dp(6));
        studioQueueAddInQueueTabBtn = PillButton(S["AddQueueBtn"], AddCurrentToQueue);
        studioBatchAddBtn = PillButton(S["BatchAddBtn"], () => OpenMultiFilesWithExplorer(AddLocalPathsToQueue));
        qRow1.AddView(studioQueueAddInQueueTabBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        qRow1.AddView(studioBatchAddBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        exportUnifiedQueueCard.AddView(qRow1);

        LinearLayout qRow2 = HRow();
        exportProcessQueueBtn = PrimaryButton(S["ProcessQueueBtn"], StartQueueProcessing);
        studioQueueRemoveBtn = PillButton(S["RemoveQueueBtn"], RemoveLastFromQueue);
        qRow2.AddView(exportProcessQueueBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.5f) { RightMargin = Dp(6) });
        qRow2.AddView(studioQueueRemoveBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        exportUnifiedQueueCard.AddView(qRow2);

        studioQueueSection.AddView(exportUnifiedQueueCard);
        page.AddView(studioQueueSection);

        RefreshQueueList();
        RefreshConverterTabState();
        SelectExportSubMode(selectedExportSubMode);
        return page;
    }

    void SelectExportSubMode(int modeIndex) {
        selectedExportSubMode = Math.Clamp(modeIndex, 0, 2);
        selectedStudioWorkflow = selectedExportSubMode;

        for (int i = 0; i < exportSubModeBtns.Count; i++) {
            bool sel = i == selectedExportSubMode;
            Button b = exportSubModeBtns[i];
            b.Background = sel
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp);
            b.SetTextColor(sel ? OneUiTheme.OnPrimary : ColText);
            b.SetTypeface(null, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        if (studioMasterSection != null) {
            studioMasterSection.Visibility = selectedExportSubMode == 0 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (studioClassicSection != null) {
            studioClassicSection.Visibility = selectedExportSubMode == 1 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (exportExtractSection != null) {
            exportExtractSection.Visibility = selectedExportSubMode == 2 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (exportFooterCard != null) {
            exportFooterCard.Visibility = selectedExportSubMode == 2 ? ViewStates.Gone : ViewStates.Visible;
        }

        RefreshQueueList();
        RefreshStudioPrimaryCtaUi();
        RefreshStudioReadinessUi();
        if (selectedExportSubMode == 2) {
            RefreshExtractAiChainPreviewUi();
        }
    }

    void RefreshExportSourceFileBar(string displayName, string subtitle) {
        if (!string.IsNullOrWhiteSpace(displayName)) {
            SetWorkspaceHeaderText(displayName);
        }
        if (subtitle != null) {
            SetWorkspaceSummaryText(subtitle);
        }
    }

    void SyncUnifiedExportQueueUi() {
        int qCount = renderQueue?.Jobs.Count ?? 0;
        bool busy = isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive;
        if (exportQueueBadgeTv != null) {
            exportQueueBadgeTv.Text = qCount > 0
                ? string.Format(S["ExportQueueCountFmt"], qCount)
                : S["QueueEmpty"];
        }
        if (exportProcessQueueBtn != null) {
            exportProcessQueueBtn.Text = qCount > 0 ? $"{S["ProcessQueueBtn"]} ({qCount})" : S["ProcessQueueBtn"];
            exportProcessQueueBtn.Enabled = !busy && qCount > 0;
        }
    }

    void RunBackgroundOperation(Action<CancellationToken> work, string singleOutputPath) {
        activeCts?.Dispose();
        activeCts = new CancellationTokenSource();
        CancellationToken token = activeCts.Token;
        lastExportSourcePath = "";

        isOperationRunning = true;
        RunOnUiThread(() => {
            HideLastExportActionBar();
            SetRunningUiState(true);
        });
        Task.Run(() => {
            AndroidRuntimeTelemetry.ApplyBackgroundThreadQoS();
            try {
                work(token);
                if (!string.IsNullOrEmpty(singleOutputPath) &&
                    !string.Equals(lastExportSourcePath, singleOutputPath, StringComparison.Ordinal) &&
                    File.Exists(singleOutputPath)) {
                    try {
                        ExportMetadataPackage classicMeta = BuildExportMetadataPackageForActiveTrack(GetActiveTrack());
                        ExportMetadataEmbedder.EmbedInPlace(singleOutputPath, classicMeta);
                    } catch (Exception metaEx) {
                        AppLogger.Warn($"Background export metadata embed skipped: {metaEx.Message}");
                    }
                    ExportPublishResult pub = AndroidFileHelper.PublishExportedAudio(this, singleOutputPath, GetSelectedExportDestination());
                    string displayPath = !string.IsNullOrEmpty(pub?.LocalPath) ? pub.LocalPath : singleOutputPath;
                    lastExportSourcePath = singleOutputPath;
                    RunOnUiThread(() => ShowPublishedExportActions(pub, displayPath));
                }
            } catch (System.OperationCanceledException) {
                AppLogger.Info("Background render operation cancelled by user.");
                RunOnUiThread(() => SetConvertStatus(S["CancelledStatus"], 0));
            } catch (Exception ex) {
                AppLogger.Error("RunBackgroundOperation", ex);
                RunOnUiThread(() => {
                    SetConvertStatus(ex.Message, 0);
                    ShowError(ex.Message);
                });
            } finally {
                isOperationRunning = false;
                RunOnUiThread(() => SetRunningUiState(false));
            }
        });
    }

    void SetRunningUiState(bool running) {
        if (renderBtn != null) renderBtn.Enabled = !running;
        if (studioW12ExportBtn != null) studioW12ExportBtn.Enabled = !running;
        if (studioW12BatchBtn != null) studioW12BatchBtn.Enabled = !running;
        if (selectTargetBtn != null) selectTargetBtn.Enabled = !running;
        if (applyRoomToExportCheck != null) applyRoomToExportCheck.Enabled = !running;
        if (studioQueueAddBtn != null) studioQueueAddBtn.Enabled = !running;
        if (studioQueueAddInQueueTabBtn != null) studioQueueAddInQueueTabBtn.Enabled = !running;
        if (studioBatchAddBtn != null) studioBatchAddBtn.Enabled = !running;
        if (studioQueueRemoveBtn != null) studioQueueRemoveBtn.Enabled = !running;
        if (studioMetaBtn != null) studioMetaBtn.Enabled = !running;
        if (studioReportBtn != null) studioReportBtn.Enabled = !running;
        if (wiringBtn != null) {
            wiringBtn.Enabled = !running && RenderTarget is not DriverRenderTarget && RenderTarget is not VirtualizerRenderTarget;
        }
        for (int i = 0; i < studioWorkflowPillBtns.Count; i++) {
            studioWorkflowPillBtns[i].Enabled = !running;
        }
        SetStudioExportControlsBusy(running);
        if (exportFormatSelector != null) {
            exportFormatSelector.Enabled = !running;
            exportFormatSelector.Alpha = running ? 0.55f : 1f;
        }
        if (containerSelector != null) {
            containerSelector.Enabled = !running;
            containerSelector.Alpha = running ? 0.55f : 1f;
        }
        if (cancelRenderBtn != null) cancelRenderBtn.Enabled = running || ExportMediaProcessingService.IsExportActive;
        if (extractBtn != null) extractBtn.Enabled = !running;
        if (cancelExtractBtn != null) cancelExtractBtn.Enabled = running;
        if (extractJsonCheck != null) extractJsonCheck.Enabled = !running;
        if (extractResidualCheck != null) extractResidualCheck.Enabled = !running;
        for (int i = 0; i < extractBitButtons.Count; i++) {
            extractBitButtons[i].Enabled = !running && selectedExtractMode != 3;
        }
        if (running) {
            pipelineStartedUtc = DateTime.UtcNow;
            lastPipelineStageIndex = 0;
            UpdatePipelineStageVisuals(0, 0.0, isCompleted: false, visible: true);
        } else if (pipelineStageContainer != null && lastPipelineStageIndex < 0) {
            pipelineStageContainer.Visibility = ViewStates.Gone;
        }
        RefreshStudioPrimaryCtaUi();
    }

    void SetConvertStatus(string text, double progress) {
        if (convertStatusText != null) {
            convertStatusText.Text = text ?? "";
        }
        if (convertProgressBar != null) {
            if (progress < 0) {
                convertProgressBar.Indeterminate = true;
            } else {
                convertProgressBar.Indeterminate = false;
                convertProgressBar.Progress = Math.Clamp((int)Math.Round(progress * 1000.0), 0, 1000);
            }
        }
        bool isDone = !isOperationRunning && progress >= 0.999;
        bool shouldShow = isOperationRunning || isDone;
        if (shouldShow) {
            int stage = ResolvePipelineStageIndex(text ?? "", progress, isDone);
            lastPipelineStageIndex = stage;
            UpdatePipelineStageVisuals(stage, progress, isDone, visible: true);
        } else if (pipelineStageContainer != null) {
            lastPipelineStageIndex = -1;
            pipelineStageContainer.Visibility = ViewStates.Gone;
        }
    }

    static int ResolvePipelineStageIndex(string statusText, double progress, bool isDone) {
        if (isDone) {
            return 4;
        }
        string t = (statusText ?? "").ToLowerInvariant();
        if (t.Contains("encod") || t.Contains("mux") || t.Contains("flac") || t.Contains("export") ||
            t.Contains("saving") || t.Contains("salvando") || t.Contains("finaliz") || t.Contains("merging") ||
            t.Contains("gravando") || t.Contains("packing")) {
            return 3;
        }
        if (t.Contains("render") || t.Contains("spatial") || t.Contains("espacial") || t.Contains("upmix") ||
            t.Contains("binaural") || t.Contains("atmos") || t.Contains("master") || t.Contains("convolv") ||
            t.Contains("loudness") || t.Contains("iamf")) {
            return 2;
        }
        if (t.Contains("neural") || t.Contains("onnx") || t.Contains("scnet") || t.Contains("vocal") ||
            t.Contains("stem") || t.Contains("separat") || t.Contains("isolat") || t.Contains("isoland") ||
            t.Contains("karaoke") || t.Contains("demux") || t.Contains("dna")) {
            return 1;
        }
        if (t.Contains("analy") || t.Contains("analis") || t.Contains("decod") || t.Contains("probe") ||
            t.Contains("truehd") || t.Contains("prepar") || t.Contains("load") || t.Contains("carreg")) {
            return 0;
        }
        if (progress < 0) {
            return 0;
        }
        if (progress >= 0.85) return 3;
        if (progress >= 0.45) return 2;
        if (progress >= 0.15) return 1;
        return 0;
    }

    void UpdatePipelineStageVisuals(int activeStage, double progress, bool isCompleted, bool visible) {
        if (pipelineStageContainer == null) {
            return;
        }
        pipelineStageContainer.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (!visible) {
            return;
        }
        string[] stageTitles = [
            S["PipelineStageAnalyzing"],
            S["PipelineStageIsolating"],
            S["PipelineStageRendering"],
            S["PipelineStageExporting"],
            S["PipelineStageDone"]
        ];
        int titleIdx = Math.Clamp(isCompleted ? 4 : activeStage, 0, 4);
        if (pipelineStageTitleTv != null) {
            pipelineStageTitleTv.Text = stageTitles[titleIdx];
            pipelineStageTitleTv.SetTextColor(isCompleted ? OneUiTheme.FunctionalGreen : OneUiTheme.PrimaryHigh);
        }
        if (pipelineStagePctTv != null) {
            string elapsed = pipelineStartedUtc != DateTime.MinValue
                ? $" · {Math.Max(0, (int)(DateTime.UtcNow - pipelineStartedUtc).TotalSeconds)}s"
                : "";
            if (isCompleted) {
                pipelineStagePctTv.Text = $"100%{elapsed}";
                pipelineStagePctTv.SetTextColor(OneUiTheme.FunctionalGreen);
            } else if (progress < 0) {
                pipelineStagePctTv.Text = $"...{elapsed}";
                pipelineStagePctTv.SetTextColor(OneUiTheme.PrimaryHigh);
            } else {
                int pct = Math.Clamp((int)Math.Round(progress * 100.0), 0, 100);
                pipelineStagePctTv.Text = $"{pct}%{elapsed}";
                pipelineStagePctTv.SetTextColor(OneUiTheme.TextPrimary);
            }
        }
        for (int i = 0; i < 4; i++) {
            bool stepDone = isCompleted || i < activeStage;
            bool stepActive = !isCompleted && i == activeStage;
            if (pipelineStageBars[i] != null) {
                Color barCol = stepDone
                    ? OneUiTheme.FunctionalGreen
                    : (stepActive ? OneUiTheme.PrimaryHigh : OneUiTheme.SurfaceModalRow);
                pipelineStageBars[i].Background = OneUiTheme.CreateRoundedDrawable(this, barCol, 4f);
            }
            if (pipelineStageLabels[i] != null) {
                Color lblCol = stepDone
                    ? OneUiTheme.FunctionalGreen
                    : (stepActive ? OneUiTheme.TextPrimary : ColMuted);
                pipelineStageLabels[i].SetTextColor(lblCol);
                pipelineStageLabels[i].SetTypeface(null, stepActive ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            }
        }
    }

    void HideLastExportActionBar() {
        if (locateExportBtn != null) locateExportBtn.Visibility = ViewStates.Gone;
        if (shareExportBtn != null) shareExportBtn.Visibility = ViewStates.Gone;
        if (playExportBtn != null) playExportBtn.Visibility = ViewStates.Gone;
        if (lastExportPathText != null) lastExportPathText.Visibility = ViewStates.Gone;
    }

    void ShowPublishedExportActions(ExportPublishResult publish, string fallbackFilePath) {
        lastExportContentUri = publish?.ContentUri;
        lastRenderedFilePath = !string.IsNullOrEmpty(publish?.LocalPath) && File.Exists(publish.LocalPath)
            ? publish.LocalPath
            : (!string.IsNullOrEmpty(fallbackFilePath) && File.Exists(fallbackFilePath) ? fallbackFilePath : "");
        lastExportFolderAbsPath = !string.IsNullOrEmpty(publish?.PublicFolderAbsolutePath)
            ? publish.PublicFolderAbsolutePath
            : (!string.IsNullOrEmpty(lastRenderedFilePath) ? Path.GetDirectoryName(lastRenderedFilePath) ?? "" : "");
        if (!string.IsNullOrEmpty(publish?.PublicDisplayPath)) {
            lastExportPublicDisplayPath = publish.PublicDisplayPath;
        } else {
            string fileName = !string.IsNullOrEmpty(lastRenderedFilePath) ? Path.GetFileName(lastRenderedFilePath) : "";
            string folderRel = !string.IsNullOrEmpty(publish?.FolderDisplayPath)
                ? publish.FolderDisplayPath
                : (GetSelectedExportDestination() == ExportStorageDestination.DownloadsCavernize ? "Download/Cavernize" : "Music/Cavernize");
            lastExportPublicDisplayPath = !string.IsNullOrEmpty(fileName) ? $"{folderRel}/{fileName}" : folderRel;
        }

        if (lastExportPathText != null) {
            string msg = string.Format(S["ExportSavedToFmt"], lastExportPublicDisplayPath);
            if (publish?.RoutedToDownloadsForContainerCompat == true) {
                msg += $" · {S["ExportCompatDownloadsBadge"]}";
            }
            lastExportPathText.Text = msg;
            lastExportPathText.Visibility = ViewStates.Visible;
        }
        bool hasFileOrUri = !string.IsNullOrEmpty(lastRenderedFilePath) || lastExportContentUri != null;
        if (locateExportBtn != null) locateExportBtn.Visibility = hasFileOrUri ? ViewStates.Visible : ViewStates.Gone;
        if (shareExportBtn != null) shareExportBtn.Visibility = hasFileOrUri ? ViewStates.Visible : ViewStates.Gone;
        if (playExportBtn != null) playExportBtn.Visibility = hasFileOrUri ? ViewStates.Visible : ViewStates.Gone;
    }

    void OnLocateExportClicked() {
        string folderRel = lastExportPublicDisplayPath.StartsWith("Download/", StringComparison.OrdinalIgnoreCase)
            ? "Download/Cavernize"
            : "Music/Cavernize";
        bool openedSystemFiles = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
            try {
                Android.Net.Uri docUri = Android.Provider.DocumentsContract.BuildDocumentUri(
                    "com.android.externalstorage.documents",
                    $"primary:{folderRel}");
                if (docUri != null) {
                    Intent viewDir = new(Intent.ActionView);
                    viewDir.SetDataAndType(docUri, "vnd.android.document/directory");
                    viewDir.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                    StartActivity(viewDir);
                    openedSystemFiles = true;
                }
            } catch (Exception ex) {
                AppLogger.Info($"Locate system folder intent fallback: {ex.Message}");
            }
        }
        if (openedSystemFiles) {
            Toast.MakeText(this, string.Format(S["ExportSavedToFmt"], lastExportPublicDisplayPath), ToastLength.Short).Show();
            return;
        }

        string initDir = !string.IsNullOrEmpty(lastExportFolderAbsPath) && Directory.Exists(lastExportFolderAbsPath)
            ? lastExportFolderAbsPath
            : AndroidFileHelper.GetPublicExportFolderAbsolutePath(
                folderRel.StartsWith("Download/", StringComparison.OrdinalIgnoreCase)
                    ? ExportStorageDestination.DownloadsCavernize
                    : ExportStorageDestination.MusicCavernize);
        Toast.MakeText(this, string.Format(S["ExportSavedToFmt"], lastExportPublicDisplayPath), ToastLength.Long).Show();
        if (!string.IsNullOrEmpty(initDir) && Directory.Exists(initDir)) {
            OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
                Title = S["LocateExportTitle"],
                Mode = ExplorerPickMode.SingleFile,
                AllowAllFiles = true,
                InitialDirectory = initDir,
                ConfirmLabel = S["OpenPlayer"],
                OnConfirm = paths => {
                    if (paths != null && paths.Count > 0 && File.Exists(paths[0])) {
                        OpenLocalPathWithFallbackAsync(paths[0], switchToTab: 0);
                    }
                }
            });
        }
    }

    void OnShareLastExportClicked() {
        if (lastExportContentUri != null) {
            try {
                string mime = !string.IsNullOrEmpty(lastExportPublicDisplayPath)
                    ? AndroidFileHelper.GetMimeType(lastExportPublicDisplayPath)
                    : "audio/*";
                Intent share = new(Intent.ActionSend);
                share.SetType(mime);
                share.PutExtra(Intent.ExtraStream, lastExportContentUri);
                share.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(Intent.CreateChooser(share, S["ShareFile"]));
                return;
            } catch (Exception ex) {
                AppLogger.Warn($"Share ContentUri fallback: {ex.Message}");
            }
        }
        ShareLocalFile(lastRenderedFilePath);
    }

    void OnPlayLastExportClicked() {
        if (!string.IsNullOrEmpty(lastRenderedFilePath) && File.Exists(lastRenderedFilePath)) {
            OpenLocalPathWithFallbackAsync(lastRenderedFilePath, switchToTab: 0);
            return;
        }
        if (lastExportContentUri != null) {
            SwitchTab(0);
            LoadUriAsync(lastExportContentUri);
        }
    }

    void ShareLocalFile(string path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            return;
        }
        Android.Net.Uri uri = AndroidFileHelper.GetShareableUri(this, path);
        if (uri == null) {
            return;
        }
        Intent share = new(Intent.ActionSend);
        share.SetType(path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            ? "text/plain"
            : AndroidFileHelper.GetMimeType(path));
        share.PutExtra(Intent.ExtraStream, uri);
        share.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(Intent.CreateChooser(share, S["ShareFile"]));
    }
}
