using System.Numerics;
using System.Text;

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Container;
using Cavern.Format.Environment;
using Cavern.Format.Exceptions;
using Cavern.Format.Renderers;
using Cavern.Utilities;
using Cavern.Virtualizer;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    Button selectTargetBtn, wiringBtn, renderBtn, cancelRenderBtn, locateExportBtn, shareExportBtn, playExportBtn, studioW12ExportBtn, studioW12BatchBtn;
    Button studioQueueAddBtn, studioQueueAddInQueueTabBtn, studioBatchAddBtn, studioQueueRemoveBtn, studioMetaBtn, studioReportBtn;
    Button studioBadgeFfmpegBtn, studioBadgeTruehdBtn, studioBadgeOnnxBtn, studioBadgeTwinBtn, studioPrereqJumpBtn;
    LayoutDiagramView layoutDiagram;
    TextView convertStatusText, studioW12ContextText, lastExportPathText;
    Android.Net.Uri lastExportContentUri;
    string lastExportFolderAbsPath = "";
    string lastExportPublicDisplayPath = "";
    string lastExportSourcePath = "";
    OneUiInlineSelector studioWorkflowSelector, exportFormatSelector, containerSelector;
    readonly List<Button> studioWorkflowPillBtns = [];
    LinearLayout studioMasterSection, studioClassicSection, studioQueueSection;
    LinearLayout studioProContentContainer;
    OneUiIconView studioProChevron;
    bool isStudioProExpanded;
    int selectedStudioWorkflow; // 0 = Master, 1 = Classic, 2 = Queue
    LinearLayout studioW12KaraokeContainer;
    OneUiSeekBar studioW12KaraokeBar;
    CheckBox applyRoomToExportCheck;
    ProgressBar convertProgressBar;
    LinearLayout pipelineStageContainer;
    TextView pipelineStageTitleTv;
    TextView pipelineStagePctTv;
    readonly View[] pipelineStageBars = new View[4];
    readonly TextView[] pipelineStageLabels = new TextView[4];
    DateTime pipelineStartedUtc = DateTime.MinValue;
    int lastPipelineStageIndex = -1;
    Action pendingPostTrueHDStudioAction;
    CavernizeTrack pendingTrueHDStudioTrack;

    List<string> availableContainerExts = [".wav"];
    int blockSize;
    const string waveExtension = ".wav";
    const int secondFrame = 2 * 1536;
    const double progressSplit = .95;

    void ApplyTabPageCutoutPadding(LinearLayout page) {
        if (page == null) {
            return;
        }
        void UpdatePad() {
            if (uiDestroyed) {
                return;
            }
            int cutoutL = 0;
            int cutoutR = 0;
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                var cutout = Window?.DecorView?.RootWindowInsets?.DisplayCutout;
                if (cutout != null) {
                    cutoutL = Math.Max(0, cutout.SafeInsetLeft - insetLeftPx);
                    cutoutR = Math.Max(0, cutout.SafeInsetRight - insetRightPx);
                }
            }
            int targetL = Dp(14) + cutoutL;
            int targetR = Dp(14) + cutoutR;
            int targetB = Dp(116) + Math.Max(insetBarsBottomPx, insetImeBottomPx);
            if (page.PaddingLeft != targetL || page.PaddingRight != targetR || page.PaddingBottom < Dp(116)) {
                page.SetPadding(targetL, Dp(6), targetR, Math.Max(page.PaddingBottom, targetB));
            }
        }
        UpdatePad();
        page.LayoutChange += (_, _) => UpdatePad();
    }

    LinearLayout BuildConverterTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        ApplyTabPageCutoutPadding(page);

        // 1. Segmented Workflow Selector (Master | Classic | Queue) + Readiness Pills Card (WorkspaceSourceBar hoisted above contentFrame)
        LinearLayout modeCard = Card();
        studioWorkflowSelector = new OneUiInlineSelector(this, S["StudioWorkflowLabel"]);
        string[] workflowNames = [
            S["StudioModeMaster"],
            S["StudioModeClassic"],
            S["StudioModeQueue"]
        ];
        studioWorkflowSelector.SetItems(workflowNames, selectedStudioWorkflow);
        studioWorkflowSelector.SelectionChanged += idx => SetStudioWorkflowMode(idx);
        modeCard.AddView(studioWorkflowSelector);

        LinearLayout segRow = HRow();
        segRow.SetPadding(0, Dp(8), 0, Dp(10));
        studioWorkflowPillBtns.Clear();
        for (int i = 0; i < workflowNames.Length; i++) {
            int modeIdx = i;
            Button pill = PillButton(workflowNames[i], () => SetStudioWorkflowMode(modeIdx), active: i == selectedStudioWorkflow);
            pill.TextSize = 11.5f;
            pill.SetPadding(Dp(8), Dp(7), Dp(8), Dp(7));
            studioWorkflowPillBtns.Add(pill);
            segRow.AddView(pill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                RightMargin = i < workflowNames.Length - 1 ? Dp(6) : 0
            });
        }
        modeCard.AddView(segRow);

        AddDivider(modeCard);
        TextView readyHdr = Label(S["StudioReadinessHeader"], 11.5f, ColMuted);
        readyHdr.SetPadding(0, Dp(2), 0, Dp(6));
        modeCard.AddView(readyHdr);

        LinearLayout badgeRow1 = HRow();
        studioBadgeTruehdBtn = PillButton(S["StudioBadgeTrueHdReady"], OnTrueHdReadinessBadgeClicked);
        studioBadgeFfmpegBtn = PillButton(S["StudioBadgeFFmpegReady"], () => SwitchTab(4));
        studioBadgeTruehdBtn.TextSize = 11f;
        studioBadgeFfmpegBtn.TextSize = 11f;
        badgeRow1.AddView(studioBadgeTruehdBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        badgeRow1.AddView(studioBadgeFfmpegBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        modeCard.AddView(badgeRow1, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        LinearLayout badgeRow2 = HRow();
        studioBadgeOnnxBtn = PillButton(S["StudioBadgeOnnxReady"], OnOnnxReadinessBadgeClicked);
        studioBadgeTwinBtn = PillButton(S["StudioBadgeTwinNone"], OnTwinReadinessBadgeClicked);
        studioBadgeOnnxBtn.TextSize = 11f;
        studioBadgeTwinBtn.TextSize = 11f;
        badgeRow2.AddView(studioBadgeOnnxBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        badgeRow2.AddView(studioBadgeTwinBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        modeCard.AddView(badgeRow2);

        studioPrereqJumpBtn = SecondaryButton(S["OpenSettingsBtn"], OnStudioPrerequisiteJumpClicked);
        studioPrereqJumpBtn.Visibility = ViewStates.Gone;
        modeCard.AddView(studioPrereqJumpBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });
        page.AddView(modeCard);

        // 2A. Master Workflow Section (W48 Vocal Performance Recorder + Composable Spatial Export Builder)
        studioMasterSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        studioMasterSection.AddView(BuildStudioPerformanceRecorderCard());
        studioMasterSection.AddView(BuildStudioComposableExportCard());
        page.AddView(studioMasterSection);

        // 2B. Classic Workflow Section (System Target + Codec/Container + Collapsible Pro Options)
        studioClassicSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        LinearLayout sysCard = Card(S["SystemTitle"], OneUiIconType.Speaker, OneUiTheme.IconSpatial);
        LinearLayout sysRow = HRow();

        LinearLayout sysLeft = new(this) { Orientation = Orientation.Vertical };
        LinearLayout rtLabelRow = HRow();
        rtLabelRow.AddView(Label(S["RenderTarget"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        rtLabelRow.AddView(CreateInlineHelpBtn(S["RenderTarget"].TrimEnd(':'), S["TipRenderTarget"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        sysLeft.AddView(rtLabelRow);

        selectTargetBtn = SecondaryButton(RenderTarget?.Name ?? S["SelectTarget"], ShowRenderTargetDialog);
        sysLeft.AddView(selectTargetBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        sysRow.AddView(sysLeft, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(10) });

        layoutDiagram = new LayoutDiagramView(this);
        sysRow.AddView(layoutDiagram);
        sysCard.AddView(sysRow);
        studioClassicSection.AddView(sysCard);

        LinearLayout expCard = Card(S["ExportCardTitle"], OneUiIconType.Bolt, OneUiTheme.IconStudioExport);
        exportFormatSelector = new OneUiInlineSelector(this, S["ExportFormat"]);
        ExportFormat[] formats = ExportFormat.GetFormats();
        exportFormatSelector.SetItems(
            formats.Select(f => f.Description).ToArray(),
            Math.Clamp(Array.IndexOf(formats, ExportFormat), 0, formats.Length - 1));
        exportFormatSelector.SelectionChanged += pos => {
            ExportFormat[] curFormats = ExportFormat.GetFormats();
            if (pos >= 0 && pos < curFormats.Length) {
                ExportFormat = curFormats[pos];
                store.ExportFormatIndex = pos;
                RefreshContainerOptions();
                RefreshStudioReadinessUi();
            }
        };
        expCard.AddView(exportFormatSelector);

        AddDivider(expCard);
        containerSelector = new OneUiInlineSelector(this, S["Container"]);
        containerSelector.SelectionChanged += _ => RefreshStudioReadinessUi();
        expCard.AddView(containerSelector);

        studioQueueAddBtn = PillButton(S["AddQueueBtn"], AddCurrentToQueue);
        expCard.AddView(studioQueueAddBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(10),
            BottomMargin = Dp(4)
        });

        AddDivider(expCard);
        LinearLayout proHeader = HRow();
        proHeader.SetPadding(0, Dp(4), 0, Dp(4));
        OneUiTheme.AttachRecoil(proHeader, 0.98f);
        proHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Wrench, OneUiTheme.IconSystemInfo, 26));
        TextView proTitleTv = Label(S["StudioProSectionTitle"], 12.8f, ColText);
        proTitleTv.Typeface = OneUiTheme.GetAppleFontBold(this);
        proTitleTv.SetPadding(Dp(8), 0, Dp(8), 0);
        proHeader.AddView(proTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        studioProChevron = new OneUiIconView(this, isStudioProExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown, OneUiTheme.PeachText);
        proHeader.AddView(studioProChevron, new LinearLayout.LayoutParams(Dp(16), Dp(16)));
        expCard.AddView(proHeader);

        studioProContentContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isStudioProExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        studioProContentContainer.SetPadding(0, Dp(8), 0, 0);
        proHeader.Click += (_, _) => {
            isStudioProExpanded = !isStudioProExpanded;
            SetExpandedWithAnimation(studioProContentContainer, isStudioProExpanded);
            studioProChevron.IconType = isStudioProExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
        };

        applyRoomToExportCheck = new CheckBox(this) {
            Text = S["ApplyRoomToExport"],
            Checked = false,
            TextSize = 12.5f
        };
        applyRoomToExportCheck.SetTextColor(ColText);
        applyRoomToExportCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
        studioProContentContainer.AddView(WrapControlWithHelp(applyRoomToExportCheck, S["ApplyRoomToExport"], S["TipApplyRoomExport"], bottomMarginDp: 6));

        wiringBtn = PillButton(S["DisplayWiring"], DisplayWiringDialog);
        studioProContentContainer.AddView(wiringBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        LinearLayout infoBtns = HRow();
        studioMetaBtn = PillButton(S["ShowMetaBtn"], ShowMetadataDialog);
        studioReportBtn = PillButton(S["ShowReportBtn"], ShowPostRenderReportDialog);
        infoBtns.AddView(studioMetaBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        infoBtns.AddView(studioReportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        studioProContentContainer.AddView(infoBtns);

        expCard.AddView(studioProContentContainer);
        studioClassicSection.AddView(expCard);
        page.AddView(studioClassicSection);

        // 2C. Queue Workflow Section (Batch Render Queue — 2-row non-clipping secondary buttons)
        studioQueueSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        LinearLayout queueCard = Card(S["QueueTitle"], OneUiIconType.Queue, OneUiTheme.IconAtmosUpmix);
        queueListContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        queueCard.AddView(queueListContainer);

        LinearLayout qRow1 = HRow();
        qRow1.SetPadding(0, Dp(10), 0, Dp(6));
        studioQueueAddInQueueTabBtn = PillButton(S["AddQueueBtn"], AddCurrentToQueue);
        studioBatchAddBtn = PillButton(S["BatchAddBtn"], () => OpenMultiFilesWithExplorer(AddLocalPathsToQueue));
        qRow1.AddView(studioQueueAddInQueueTabBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        qRow1.AddView(studioBatchAddBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        queueCard.AddView(qRow1);

        studioQueueRemoveBtn = PillButton(S["RemoveQueueBtn"], RemoveLastFromQueue);
        queueCard.AddView(studioQueueRemoveBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        studioQueueSection.AddView(queueCard);
        page.AddView(studioQueueSection);

        // 3. Shared Unified Primary Action, Progress, Status, Cancel, Share & Play Footer Card
        LinearLayout footerCard = Card();
        LinearLayout actRow = HRow();
        actRow.SetPadding(0, Dp(2), 0, Dp(6));
        renderBtn = PrimaryButton(S["StudioW12ExportBtn"], ExecuteActiveStudioPrimaryAction);
        studioW12ExportBtn = renderBtn;
        cancelRenderBtn = PillButton(S["CancelBtn"], () => {
            if (ExportMediaProcessingService.IsExportActive) {
                ExportMediaProcessingService.CancelActiveExport();
            }
            activeCts?.Cancel();
        });
        cancelRenderBtn.Enabled = false;
        actRow.AddView(renderBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2.1f) { RightMargin = Dp(8) });
        actRow.AddView(cancelRenderBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        footerCard.AddView(actRow);

        // Granular 4-stage audio processing pipeline progress tracker (shown automatically while processing)
        pipelineStageContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        pipelineStageContainer.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        pipelineStageContainer.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceSubTile, 14f, OneUiTheme.BorderSubtle, 1f);

        LinearLayout stageHdr = HRow();
        pipelineStageTitleTv = Label(S["PipelineStageAnalyzing"], 11.5f, OneUiTheme.PrimaryHigh);
        pipelineStageTitleTv.SetTypeface(null, TypefaceStyle.Bold);
        pipelineStageTitleTv.SetSingleLine(true);
        pipelineStageTitleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        pipelineStagePctTv = Label("0%", 11.5f, OneUiTheme.TextPrimary);
        pipelineStagePctTv.SetTypeface(Typeface.Monospace, TypefaceStyle.Bold);
        pipelineStagePctTv.SetSingleLine(true);
        stageHdr.AddView(pipelineStageTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        stageHdr.AddView(pipelineStagePctTv);
        pipelineStageContainer.AddView(stageHdr, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        LinearLayout stageSegRow = HRow();
        LinearLayout lblRow = HRow();
        string[] stageShortNames = [
            S["PipelineStep1Short"],
            S["PipelineStep2Short"],
            S["PipelineStep3Short"],
            S["PipelineStep4Short"]
        ];
        for (int i = 0; i < 4; i++) {
            View bar = new(this) {
                Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 4f)
            };
            pipelineStageBars[i] = bar;
            stageSegRow.AddView(bar, new LinearLayout.LayoutParams(0, Dp(5), 1f) {
                RightMargin = i < 3 ? Dp(5) : 0
            });

            TextView sLbl = Label(stageShortNames[i], 10f, ColMuted);
            sLbl.SetSingleLine(true);
            sLbl.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            sLbl.Gravity = GravityFlags.Center;
            pipelineStageLabels[i] = sLbl;
            lblRow.AddView(sLbl, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                RightMargin = i < 3 ? Dp(4) : 0
            });
        }
        pipelineStageContainer.AddView(stageSegRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });
        pipelineStageContainer.AddView(lblRow);
        footerCard.AddView(pipelineStageContainer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4), BottomMargin = Dp(4) });

        convertProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0
        };
        OneUiTheme.StyleProgressBar(this, convertProgressBar);
        footerCard.AddView(convertProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)) { TopMargin = Dp(6) });

        convertStatusText = Label(S["Ready"], 12f, OneUiTheme.IconSystemInfo);
        convertStatusText.SetPadding(0, Dp(6), 0, Dp(4));
        footerCard.AddView(convertStatusText);

        lastExportPathText = Label("", 11.2f, OneUiTheme.FunctionalGreen);
        lastExportPathText.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        lastExportPathText.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceSubTile, 12f, OneUiTheme.BorderSubtle, 0.8f);
        lastExportPathText.Visibility = ViewStates.Gone;
        footerCard.AddView(lastExportPathText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        LinearLayout postBtns = HRow();
        locateExportBtn = PillButton(S["LocateExportBtn"], OnLocateExportClicked);
        shareExportBtn = PillButton(S["ShareFile"], OnShareLastExportClicked);
        playExportBtn = PillButton(S["OpenPlayer"], OnPlayLastExportClicked, active: true);
        locateExportBtn.Visibility = ViewStates.Gone;
        shareExportBtn.Visibility = ViewStates.Gone;
        playExportBtn.Visibility = ViewStates.Gone;
        postBtns.AddView(locateExportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        postBtns.AddView(shareExportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        postBtns.AddView(playExportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        footerCard.AddView(postBtns);
        page.AddView(footerCard);

        RefreshQueueList();
        RefreshConverterTabState();
        SetStudioWorkflowMode(selectedStudioWorkflow);
        return page;
    }

    void SetStudioWorkflowMode(int modeIdx) {
        selectedStudioWorkflow = Math.Clamp(modeIdx, 0, 2);
        if (studioWorkflowSelector != null && studioWorkflowSelector.SelectedIndex != selectedStudioWorkflow) {
            studioWorkflowSelector.SetSelection(selectedStudioWorkflow, notify: false);
        }
        for (int i = 0; i < studioWorkflowPillBtns.Count; i++) {
            bool sel = i == selectedStudioWorkflow;
            Button b = studioWorkflowPillBtns[i];
            b.Background = sel
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        if (studioMasterSection != null) {
            studioMasterSection.Visibility = selectedStudioWorkflow == 0 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (studioClassicSection != null) {
            studioClassicSection.Visibility = selectedStudioWorkflow == 1 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (studioQueueSection != null) {
            studioQueueSection.Visibility = selectedStudioWorkflow == 2 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (selectedStudioWorkflow == 2) {
            RefreshQueueList();
        }
        RefreshStudioPrimaryCtaUi();
        RefreshStudioReadinessUi();
    }

    void ExecuteActiveStudioPrimaryAction() {
        switch (selectedStudioWorkflow) {
            case 0:
                StartStudioW12PresetExport();
                break;
            case 1:
                StartSingleRender();
                break;
            case 2:
                StartQueueProcessing();
                break;
        }
    }

    void RefreshStudioPrimaryCtaUi() {
        if (renderBtn == null) {
            return;
        }
        bool busy = isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive;
        if (selectedStudioWorkflow == 0) {
            renderBtn.Text = S["StudioW12ExportBtn"];
            renderBtn.Enabled = !busy;
        } else if (selectedStudioWorkflow == 1) {
            renderBtn.Text = S["RenderBtn"];
            renderBtn.Enabled = !busy;
        } else {
            int qCount = renderQueue?.Jobs.Count ?? 0;
            renderBtn.Text = qCount > 0 ? $"{S["ProcessQueueBtn"]} ({qCount})" : S["ProcessQueueBtn"];
            renderBtn.Enabled = !busy && qCount > 0;
        }
    }

    bool IsLosslessTwinSynced(out string twinSummary) {
        Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        var twinRef = activeLosslessTwin ?? playerEngine?.SceneHub?.ActiveLosslessReference as Cavernize.Logic.Spatial.UniversalLosslessTwinProvider;
        bool synced = (twinRef != null && twinRef.HasSyncedLosslessTwin) ||
                      lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
        twinSummary = !string.IsNullOrEmpty(lgsr.TwinFormatSummary)
            ? lgsr.TwinFormatSummary
            : (!string.IsNullOrEmpty(lgsr.TwinFileName)
                ? lgsr.TwinFileName
                : (!string.IsNullOrEmpty(twinRef?.Metadata?.FilePath)
                    ? Path.GetFileName(twinRef.Metadata.FilePath)
                    : "FLAC"));
        return synced;
    }

    void StyleReadinessBadge(Button btn, string text, Color tint, bool highlight) {
        if (btn == null) {
            return;
        }
        btn.Text = text;
        btn.SetTextColor(highlight ? ColText : OneUiTheme.TextSecondary);
        btn.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            OneUiTheme.WithAlpha(tint, (byte)(highlight ? 64 : 32)),
            OneUiTheme.PillCornerDp,
            OneUiTheme.WithAlpha(tint, (byte)(highlight ? 175 : 95)),
            1f);
    }

    void RefreshStudioReadinessUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        string thdPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        bool thdInstalled = !string.IsNullOrEmpty(thdPath) && File.Exists(thdPath);
        bool thdPendingDecode = selectedTrack != null && selectedTrack.Codec == Codec.TrueHD && decodedTrueHDTrack == null;
        if (!thdInstalled) {
            StyleReadinessBadge(studioBadgeTruehdBtn, S["StudioBadgeTrueHdMissing"], ColDanger, true);
        } else if (thdPendingDecode) {
            StyleReadinessBadge(studioBadgeTruehdBtn, S["StudioBadgeTrueHdPending"], OneUiTheme.FunctionalOrange, true);
        } else {
            StyleReadinessBadge(studioBadgeTruehdBtn, S["StudioBadgeTrueHdReady"], OneUiTheme.FunctionalGreen, false);
        }

        bool ffReady = ffmpeg != null && ffmpeg.Found;
        StyleReadinessBadge(
            studioBadgeFfmpegBtn,
            ffReady ? S["StudioBadgeFFmpegReady"] : S["StudioBadgeFFmpegMissing"],
            ffReady ? OneUiTheme.FunctionalGreen : OneUiTheme.FunctionalOrange,
            !ffReady);

        StudioComposableExportOptions activeOpts = BuildCurrentComposableOptions();
        bool onnxReady = IsComposableExportNeuralReady(activeOpts, out _);
        StyleReadinessBadge(
            studioBadgeOnnxBtn,
            onnxReady ? S["StudioBadgeOnnxReady"] : S["StudioBadgeOnnxMissing"],
            onnxReady ? OneUiTheme.FunctionalGreen : OneUiTheme.FunctionalOrange,
            !onnxReady);

        Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        bool twinSynced = IsLosslessTwinSynced(out _);
        if (twinSynced) {
            StyleReadinessBadge(studioBadgeTwinBtn, S["StudioBadgeTwinSynced"], OneUiTheme.FunctionalGreen, true);
        } else if (lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless) {
            StyleReadinessBadge(studioBadgeTwinBtn, S["StudioBadgeTwinNative"], OneUiTheme.IconSystemInfo, false);
        } else {
            StyleReadinessBadge(studioBadgeTwinBtn, S["StudioBadgeTwinNone"], OneUiTheme.PeachText, activeOpts.EnableLosslessTwinRestoration);
        }

        if (studioPrereqJumpBtn != null) {
            bool needsOnnxNow = selectedStudioWorkflow == 0 &&
                ((activeOpts.EnableAiSpatialUpmix && !IsActiveTrackMultichannelOrAtmos()) ||
                 activeOpts.EnableKaraokeInstrumental ||
                 activeOpts.EnableLosslessTwinRestoration);
            bool needsTwinNow = selectedStudioWorkflow == 0 && activeOpts.EnableLosslessTwinRestoration;
            bool missingAny = (!thdInstalled && selectedTrack?.Codec == Codec.TrueHD) ||
                              (!ffReady && selectedStudioWorkflow == 1) ||
                              (needsOnnxNow && !onnxReady) ||
                              (needsTwinNow && !twinSynced);
            studioPrereqJumpBtn.Visibility = missingAny ? ViewStates.Visible : ViewStates.Gone;
            if (needsTwinNow && !twinSynced && GetActiveTrack() != null) {
                studioPrereqJumpBtn.Text = S["LgsrRescanTwinBtn"];
            } else {
                studioPrereqJumpBtn.Text = S["OpenSettingsBtn"];
            }
        }
    }

    void OnTrueHdReadinessBadgeClicked() {
        if (selectedTrack != null && selectedTrack.Codec == Codec.TrueHD && decodedTrueHDTrack == null) {
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
        } else {
            SwitchTab(4);
        }
    }

    void OnOnnxReadinessBadgeClicked() {
        if (OnnxRuntimeInstallerService.IsRuntimeInstalled(this)) {
            ShowNeuralModelsModal();
        } else {
            SwitchTab(4);
        }
    }

    void OnTwinReadinessBadgeClicked() {
        CavernizeTrack active = GetActiveTrack();
        if (active != null && !IsLosslessTwinSynced(out _)) {
            StartLosslessTwinDiscoveryAsync(active);
        } else {
            SwitchTab(4);
        }
    }

    void OnStudioPrerequisiteJumpClicked() {
        StudioComposableExportOptions activeOpts = BuildCurrentComposableOptions();
        if (selectedStudioWorkflow == 0 && activeOpts.EnableLosslessTwinRestoration && !IsLosslessTwinSynced(out _) && GetActiveTrack() != null) {
            StartLosslessTwinDiscoveryAsync(GetActiveTrack());
            return;
        }
        if (selectedStudioWorkflow == 0 && !IsComposableExportNeuralReady(activeOpts, out bool rtOk) && rtOk) {
            ShowNeuralModelsModal();
            return;
        }
        SwitchTab(4);
    }

    void RefreshConverterTabState() {
        if (selectTargetBtn != null && RenderTarget != null) {
            selectTargetBtn.Text = RenderTarget.Name;
        }
        layoutDiagram?.SetRenderTarget(RenderTarget);
        if (wiringBtn != null) {
            wiringBtn.Enabled = !isOperationRunning && RenderTarget is not DriverRenderTarget && RenderTarget is not VirtualizerRenderTarget;
        }
        RefreshContainerOptions();
        RefreshStudioW12ContextUi();
        RefreshStudioPrimaryCtaUi();
        RefreshStudioReadinessUi();
    }

    void RefreshStudioW12ContextUi() {
        if (store == null) {
            return;
        }
        RefreshStudioComposableExportUi();
        RefreshStudioReadinessUi();
        RefreshExtractAiChainPreviewUi();
    }

    void RefreshConverterTrackInfo() {
        RefreshConverterTabState();
        if (!isPreparingTrueHD && pendingPostTrueHDStudioAction != null) {
            Action resume = pendingPostTrueHDStudioAction;
            CavernizeTrack expected = pendingTrueHDStudioTrack;
            pendingPostTrueHDStudioAction = null;
            pendingTrueHDStudioTrack = null;
            if (decodedTrueHDTrack != null && selectedTrack == expected && !isOperationRunning) {
                uiHandler?.Post(resume);
            }
        }
    }

    void RefreshContainerOptions() {
        if (containerSelector == null || ExportFormat == null) {
            return;
        }
        List<string> labels = [];
        List<string> exts = [];

        CavernizeTrack active = GetActiveTrack();
        if (active != null) {
            try {
                string filter = MergeToContainer.GetPossibleContainers(active, ExportFormat.Codec, ffmpeg);
                string[] parts = filter.Split('|');
                for (int i = 0; i + 1 < parts.Length; i += 2) {
                    string pattern = parts[i + 1].Trim();
                    string firstExt = pattern.Split(';')[0].Replace("*", "").Trim();
                    if (!string.IsNullOrEmpty(firstExt)) {
                        labels.Add($"{parts[i]} ({firstExt})");
                        exts.Add(firstExt);
                    }
                }
            } catch {
                // Fallback below
            }
        }
        if (exts.Count == 0) {
            string ext = ExportFormat.Codec switch {
                Codec.LimitlessAudio => ".laf",
                Codec.IAMF => ".iamf",
                Codec.AmbisonicsHOA => ".amb",
                Codec.DAMF => ".atmos",
                Codec.FLAC => ".flac",
                Codec.Opus => ".opus",
                Codec.EnhancedAC3 => ".eac3",
                Codec.AC3 => ".ac3",
                _ => ".wav"
            };
            exts.Add(ext);
            labels.Add($"{ExportFormat.Codec} ({ext})");
        }

        // Default to native audio extension (.flac / .eac3 / .opus) when source is audio-only rather than video container
        int defaultIdx = 0;
        if (active == null || active.Container != Container.Matroska) {
            int nonMkv = exts.FindIndex(e => !e.Equals(".mkv", StringComparison.OrdinalIgnoreCase));
            if (nonMkv >= 0) {
                defaultIdx = nonMkv;
            }
        }

        availableContainerExts = exts;
        containerSelector.SetItems(labels.ToArray(), defaultIdx);
    }

    void ShowRenderTargetDialog() {
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };

        Dialog dialog = null;
        RenderTarget[] all = RenderTarget.Targets;
        List<RenderTarget> pc = [], matrix = [], multi = [];
        foreach (RenderTarget t in all) {
            if (t.OutputChannels <= 8) {
                if (t is DownmixedRenderTarget dm && dm.IsMatrixWired) {
                    matrix.Add(t);
                } else {
                    pc.Add(t);
                }
            } else {
                multi.Add(t);
            }
        }

        void AddCategory(string header, List<RenderTarget> items) {
            TextView hdr = SectionTitle(header);
            hdr.SetPadding(0, Dp(10), 0, Dp(6));
            list.AddView(hdr);
            foreach (RenderTarget item in items) {
                bool isSel = item == RenderTarget;
                Button b = isSel ? PrimaryButton(item.Name, null) : SecondaryButton(item.Name, null);
                b.Click += (_, _) => {
                    RenderTarget = item;
                    store.RenderTargetIndex = Array.IndexOf(all, item);
                    RefreshConverterTabState();
                    dialog?.Dismiss();
                };
                list.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
            }
        }

        AddCategory(S["CatPC"], pc);
        AddCategory(S["CatMatrix"], matrix);
        AddCategory(S["CatMulti"], multi);

        dialog = OneUiModalDialog.Show(
            this,
            S["RenderTarget"],
            customContent: list,
            icon: OneUiIconType.Speaker,
            positiveText: null,
            negativeText: S["CancelBtn"]);
    }

    void DisplayWiringDialog() {
        if (RenderTarget == null) {
            return;
        }
        ReferenceChannel[] channels = RenderTarget.GetNameMappedChannels();
        ChannelPrototype[] prototypes = ChannelPrototype.Get(channels);
        if (channels.Length > 8) {
            string order = string.Join("", prototypes.Select(x => "\n- " + x.Name));
            ShowMessageDialog(S["WrGui"], string.Format(S["Over8"], order), OneUiIconType.Speaker);
            return;
        }

        StringBuilder output = new();
        ReferenceChannel[] standard = ChannelPrototype.GetStandardMatrix(prototypes.Length);
        for (int i = 0; i < prototypes.Length; i++) {
            string chName = ChannelPrototype.Mapping[(int)channels[i]].Name;
            string stdName = ChannelPrototype.Mapping[(int)standard[i]].Name;
            output.AppendLine(string.Format(S["ChCon"], chName, stdName));
        }
        if (RenderTarget is DownmixedRenderTarget downmix) {
            var matrixed = downmix.MatrixWirings;
            for (int i = 0; i < matrixed.Length; i++) {
                output.AppendLine(string.Format(S["ChCMx"],
                    ChannelPrototype.Mapping[(int)matrixed[i].source].Name,
                    ChannelPrototype.Mapping[(int)matrixed[i].posPhase].Name,
                    ChannelPrototype.Mapping[(int)matrixed[i].negPhase].Name));
            }
        }
        ShowMessageDialog(S["WrGui"], output.ToString(), OneUiIconType.Speaker);
    }

    void ShowMetadataDialog() {
        CavernizeTrack active = GetActiveTrack();
        if (active == null) {
            ShowError(S["NoTrack"]);
            return;
        }
        ReadableMetadata meta = active.GetMetadata();
        if (meta == null) {
            ShowError(S["NoCodecMetadata"]);
            return;
        }
        StringBuilder sb = new();
        foreach (var hdr in meta.Headers) {
            sb.AppendLine(string.Format(S["CodecMetaSectionHeaderFmt"], hdr.Name, hdr.Fields.Count));
            foreach (var row in hdr.Fields) {
                sb.AppendLine($"  {row}");
            }
            sb.AppendLine();
        }
        ShowMessageDialog(S["ShowMetaBtn"], sb.ToString(), OneUiIconType.Info, monospace: true);
    }

    void ShowPostRenderReportDialog() {
        ShowMessageDialog(S["ShowReportBtn"], report?.Report ?? "", OneUiIconType.Logs, monospace: true);
    }

    // ICavernizeApp Render Pipeline
    void PreRender() {
        if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
            throw new ConcurrencyException(S["OpRunning"]);
        }
        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported) {
            throw new TrackException(S["NoTrack"]);
        }
        string chosenExt = containerSelector != null && containerSelector.SelectedIndex >= 0 && containerSelector.SelectedIndex < availableContainerExts.Count
            ? availableContainerExts[containerSelector.SelectedIndex]
            : ".wav";
        bool needsFFmpeg = !string.IsNullOrEmpty(ExportFormat.FFName) &&
            ExportFormat.Codec != Codec.PCM_Float && ExportFormat.Codec != Codec.PCM_LE;
        if ((ExportFormat.Codec == Codec.PCM_LE || ExportFormat.Codec == Codec.PCM_Float) &&
            chosenExt.Equals(".mkv", StringComparison.OrdinalIgnoreCase) &&
            active.GetVideoTracks() == null) {
            needsFFmpeg = true;
        }
        if (needsFFmpeg && ExportFormat.Codec == Codec.FLAC && chosenExt.Equals(".flac", StringComparison.OrdinalIgnoreCase)) {
            needsFFmpeg = false; // Built-in pure-C# FlacAudioConverter guarantees lossless .flac export
        }
        if (needsFFmpeg && !ffmpeg.Found) {
            throw new TrackException(string.Format(S["FFmpegNeeded"], ExportFormat.Codec));
        }
        if (active.Codec != Codec.TrueHD) {
            AttachToListener(active);
        }
    }

    void AttachToListener(CavernizeTrack track = null) {
        CavernizeTrack target = track ?? GetActiveTrack();
        store.ApplyGlobals();
        environment.AttachToListener(target);
        if (applyRoomToExportCheck != null && applyRoomToExportCheck.Checked && target.Renderer != null) {
            environment.Listener.Position = playerEngine.ListenerPosition;
            environment.Listener.Rotation = playerEngine.ListenerRotation;
            foreach (Source s in target.Renderer.Objects) {
                s.DistanceSimulation = playerEngine.DistanceSimulation && RenderTarget is VirtualizerRenderTarget;
                s.VolumeRolloff = playerEngine.VolumeRolloff;
            }
        }
    }

    public Action GetRenderTask(string path) {
        try {
            PreRender();
        } catch (Exception e) {
            ShowError(e.Message);
            return null;
        }

        CavernizeTrack target = GetActiveTrack();
        if (!store.ReportOnlyMode) {
            if (string.IsNullOrEmpty(path)) {
                path = BuildDefaultExportPath(target);
            }
            try {
                return CreateRenderAction(path, target);
            } catch (Exception e) {
                ShowError(e.Message);
                return null;
            }
        } else {
            blockSize = CavernizeOutput.GetBlockSize(RenderTarget, environment);
            return () => RenderTask(target, null, null, CancellationToken.None);
        }
    }

    public void RenderContent(string path) {
        Action task = GetRenderTask(path);
        if (task != null) {
            RunBackgroundOperation(token => task(), null);
        }
    }

    void StartSingleRender() {
        if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
            ShowError(S["OpRunning"]);
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active != null && active.Codec == Codec.TrueHD) {
            pendingTrueHDStudioTrack = selectedTrack;
            pendingPostTrueHDStudioAction = StartSingleRender;
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }
        StopPlayback();
        try {
            PreRender();
        } catch (Exception e) {
            AppLogger.Error("StartSingleRender.PreRender", e);
            ShowError(e.Message);
            return;
        }

        string outPath = store.ReportOnlyMode ? null : BuildDefaultExportPath(active);
        AppLogger.Info($"Starting single render: codec={ExportFormat.Codec}, target={RenderTarget?.Name}, out='{outPath}'");
        Action task = store.ReportOnlyMode
            ? () => RenderTask(active, null, null, activeCts?.Token ?? CancellationToken.None)
            : CreateRenderAction(outPath, active);

        RunBackgroundOperation(_ => task(), outPath);
    }

    void StartStudioW12PresetExport() => StartStudioComposableExport();

    void ShowStudioW12BatchDialog() {
        if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
            ShowError(S["OpRunning"]);
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        if (active.Codec == Codec.TrueHD) {
            pendingTrueHDStudioTrack = selectedTrack;
            pendingPostTrueHDStudioAction = ShowStudioW12BatchDialog;
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }

        StudioBaseOutputFormat[] allFormats = [
            StudioBaseOutputFormat.Headphone3D,
            StudioBaseOutputFormat.Cinema714,
            StudioBaseOutputFormat.DolbyAtmosAdm,
            StudioBaseOutputFormat.EclipsaIamf,
            StudioBaseOutputFormat.Ambisonics360
        ];

        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        CheckBox[] boxes = new CheckBox[allFormats.Length];
        int currentSelected = Math.Clamp(store?.StudioBaseFormatIndex ?? 0, 0, allFormats.Length - 1);
        for (int i = 0; i < allFormats.Length; i++) {
            StudioBaseOutputFormat fmt = allFormats[i];
            CheckBox cb = new(this) {
                Text = GetStudioBaseFormatLabel(fmt),
                Checked = i == currentSelected || fmt == StudioBaseOutputFormat.Headphone3D || fmt == StudioBaseOutputFormat.EclipsaIamf,
                TextSize = 13f
            };
            cb.SetTextColor(ColText);
            cb.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
            cb.SetPadding(Dp(6), Dp(6), Dp(4), Dp(6));
            content.AddView(cb);
            boxes[i] = cb;
        }

        OneUiModalDialog.Show(
            this,
            S["StudioW12BatchDialogTitle"],
            S["StudioBatchDialogDescClean"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconStudioExport,
            positiveText: S["StudioW12BatchBtn"],
            onPositive: () => {
                List<StudioBaseOutputFormat> chosen = new();
                for (int i = 0; i < allFormats.Length; i++) {
                    if (boxes[i].Checked) {
                        chosen.Add(allFormats[i]);
                    }
                }
                if (chosen.Count == 0) {
                    ShowError(S["StudioW12BatchSelectAtLeastOne"]);
                    return;
                }
                StartStudioW12BatchExport(GetActiveTrack(), chosen);
            },
            negativeText: S["CancelBtn"]);
    }

    void StartStudioW12BatchExport(CavernizeTrack active, List<StudioBaseOutputFormat> formats) {
        if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
            ShowError(S["OpRunning"]);
            return;
        }
        if (active == null || !active.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        if (active.Codec == Codec.TrueHD) {
            List<StudioBaseOutputFormat> captured = [.. formats];
            pendingTrueHDStudioTrack = selectedTrack;
            pendingPostTrueHDStudioAction = () => StartStudioW12BatchExport(GetActiveTrack(), captured);
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }
        if (formats == null || formats.Count == 0) {
            return;
        }

        // Stop any active recording cleanly before exporting so the recorded take is finalized
        if (playerEngine?.LiveMicBridge?.IsRecordingPerformance == true) {
            StopStudioPerformanceRecording(autoSelectInExport: true);
        }

        StudioComposableExportOptions baseOpts = BuildCurrentComposableOptions(buildChoreoIfMissing: true);
        if (formats.Any(f => StudioBatchExporter.IsAdmCompressionLocked(f, baseOpts.CompressionTier))) {
            ShowError(S["StudioAtmosTierLockWarn"]);
            return;
        }
        if (baseOpts.IncludeRecordedPerformance && !(playerEngine?.LiveMicBridge?.HasRecordedPerformance ?? false)) {
            ShowError(S["StudioRecNeedTakeToast"]);
            return;
        }
        if (baseOpts.EnableAiSpatialUpmix && !IsActiveTrackMultichannelOrAtmos() &&
            !IsComposableExportNeuralReady(baseOpts, out bool runtimeInstalled)) {
            ShowError(runtimeInstalled ? S["NeuralModelNeedInstall"] : S["NeuralRuntimeNeedInstall"]);
            return;
        }
        if (baseOpts.EnableLosslessTwinRestoration && !IsLosslessTwinSynced(out _)) {
            ShowError(S["LgsrMasterNeedsTwin"]);
            return;
        }

        ExportStorageDestination dest = GetSelectedExportDestination();
        string preflightExt = StudioBatchExporter.GetEffectiveOutputExtension(formats[0], baseOpts.CompressionTier, true);
        string preflightDir = AndroidFileHelper.GetExportWorkingDir(this, dest, preflightExt, out _, out _);
        long estTotalBytes = 0;
        for (int i = 0; i < formats.Count; i++) {
            StudioComposableExportOptions estOpts = new() {
                BaseFormat = formats[i],
                CompressionTier = baseOpts.CompressionTier
            };
            estTotalBytes += EstimateStudioExportBytes(active, estOpts);
        }
        if (!ExportMediaProcessingService.CheckFreeSpacePreflight(preflightDir, estTotalBytes, out string spaceErr)) {
            ShowError(string.IsNullOrWhiteSpace(spaceErr) ? S["StudioExportLowSpaceError"] : spaceErr);
            return;
        }

        playerEngine?.SetPerformancePreviewTake(null, enabled: false);
        StopPlayback();
        string baseName = Path.GetFileNameWithoutExtension(currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? "master");

        RunBackgroundOperation(token => {
            RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
            ExportMetadataPackage sharedMetadata = BuildExportMetadataPackageForActiveTrack(active);
            List<StudioBatchExportJob> jobs = new(formats.Count);
            for (int i = 0; i < formats.Count; i++) {
                string fmtExt = StudioBatchExporter.GetEffectiveOutputExtension(formats[i], baseOpts.CompressionTier, true);
                string fmtDir = AndroidFileHelper.GetExportWorkingDir(this, dest, fmtExt, out _, out _);
                StudioComposableExportOptions jobOpts = new() {
                    BaseFormat = formats[i],
                    CompressionTier = baseOpts.CompressionTier,
                    DynamicChoreoMode = baseOpts.DynamicChoreoMode,
                    LyricMoodDigests = baseOpts.LyricMoodDigests,
                    FfmpegExecutablePath = baseOpts.FfmpegExecutablePath,
                    ChoreoTrack = baseOpts.ChoreoTrack,
                    OnSigpipeShield = baseOpts.OnSigpipeShield,
                    EnableAiSpatialUpmix = baseOpts.EnableAiSpatialUpmix,
                    EnableKaraokeInstrumental = baseOpts.EnableKaraokeInstrumental,
                    IncludeRecordedPerformance = baseOpts.IncludeRecordedPerformance,
                    EnableLosslessTwinRestoration = baseOpts.EnableLosslessTwinRestoration,
                    ApplyPersonalizedHeadphonePost = baseOpts.ApplyPersonalizedHeadphonePost,
                    RecordedPerformanceStartTrackSample = baseOpts.RecordedPerformanceStartTrackSample,
                    RecordedPerformanceSampleRate = baseOpts.RecordedPerformanceSampleRate
                };
                jobs.Add(BuildComposableExportJob(jobOpts, fmtDir, baseName, sharedMetadata));
            }

            List<StudioBatchExportResult> results = StudioBatchExporter.ExportBatch(
                active,
                jobs,
                (idx, total, prog) => {
                    string label = idx >= 0 && idx < jobs.Count
                        ? GetStudioBaseFormatLabel(jobs[idx].ComposableOptions?.BaseFormat ?? StudioBaseOutputFormat.Headphone3D)
                        : string.Empty;
                    double overall = total > 0 ? Math.Clamp((idx + prog) / total, 0.0, 1.0) : prog;
                    RunOnUiThread(() => SetConvertStatus(
                        string.Format(S["StudioW12BatchProgressFormat"], idx + 1, total, label),
                        overall));
                },
                token);

            int okCount = 0;
            bool anyCancelled = token.IsCancellationRequested;
            string lastOkPath = null;
            ExportPublishResult lastPub = null;
            string firstError = null;
            for (int i = 0; i < results.Count; i++) {
                StudioBatchExportResult r = results[i];
                if (r.IsCancelled) {
                    anyCancelled = true;
                }
                if (r.Succeeded && !r.IsCancelled && !string.IsNullOrEmpty(r.OutputPath) && File.Exists(r.OutputPath)) {
                    okCount++;
                    ExportPublishResult pub = AndroidFileHelper.PublishExportedAudio(this, r.OutputPath, dest);
                    lastPub = pub;
                    lastOkPath = !string.IsNullOrEmpty(pub?.LocalPath) ? pub.LocalPath : r.OutputPath;
                } else if (!r.IsCancelled && firstError == null && !string.IsNullOrEmpty(r.ErrorMessage)) {
                    firstError = r.ErrorMessage;
                }
            }

            if (anyCancelled && okCount == 0) {
                throw new System.OperationCanceledException(token);
            }
            if (okCount == 0 && !string.IsNullOrEmpty(firstError)) {
                throw new InvalidOperationException(firstError);
            }
            RunOnUiThread(() => {
                SetConvertStatus(string.Format(S["StudioW12BatchDoneFormat"], okCount, results.Count), 1.0);
                if (!string.IsNullOrEmpty(lastOkPath) || lastPub != null) {
                    ShowPublishedExportActions(lastPub, lastOkPath);
                }
            });
        }, null);
    }

    string BuildDefaultExportPath(CavernizeTrack track) {
        int selIdx = containerSelector != null ? Math.Clamp(containerSelector.SelectedIndex, 0, Math.Max(0, availableContainerExts.Count - 1)) : 0;
        string ext = availableContainerExts.Count > 0 ? availableContainerExts[selIdx] : ".wav";
        string dir = AndroidFileHelper.GetExportWorkingDir(this, GetSelectedExportDestination(), ext, out _, out _);
        string fallbackSource = Path.GetFileNameWithoutExtension(currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? "render");
        ExportMetadataPackage quickMeta = currentMediaMeta != null
            ? new ExportMetadataPackage { Title = currentMediaMeta.Title ?? "", Artist = currentMediaMeta.Artist ?? "" }
            : null;
        string targetLabel = RenderTarget?.Name ?? ExportFormat?.Description ?? "Spatial";
        string cleanFileName = ExportFileNameFormatter.FormatClassicFileName(quickMeta, fallbackSource, targetLabel, ext);
        return ExportFileNameFormatter.ResolveUniqueOutputPath(dir, cleanFileName);
    }

    Action CreateRenderAction(string path, CavernizeTrack target = null) {
        target ??= GetActiveTrack();
        Codec codec = ExportFormat.Codec;
        BitDepth bits = codec == Codec.PCM_Float ? BitDepth.Float32 : store.Force24Bit ? BitDepth.Int24 : BitDepth.Int16;

        if (target.Codec == Codec.TrueHD) {
            if (codec.IsEnvironmental()) {
                return () => TranscodeDeferredTrueHDTask(target, path, codec, bits, activeCts?.Token ?? CancellationToken.None);
            }
            blockSize = CavernizeOutput.GetBlockSize(RenderTarget, environment);
            return () => RenderDeferredTrueHDTask(target, path, codec, bits, activeCts?.Token ?? CancellationToken.None);
        }

        if (codec.IsEnvironmental()) {
            return () => {
                EnvironmentWriter transcoder = EnvironmentWriter.Create(path, codec, environment.Listener, target.Length, bits, target.Renderer);
                TranscodeTask(target, transcoder, path, activeCts?.Token ?? CancellationToken.None);
            };
        }

        blockSize = CavernizeOutput.GetBlockSize(RenderTarget, environment);
        AudioWriter writer = CavernizeOutput.CreateRenderOutput(this, path, environment, target, codec, bits);
        return () => RenderTask(target, writer, path, activeCts?.Token ?? CancellationToken.None);
    }

    void RenderDeferredTrueHDTask(CavernizeTrack rawTrueHDTarget, string finalName, Codec codec, BitDepth bits, CancellationToken token) {
        using ExternalConverterHandler external = CreateExternalHandler(rawTrueHDTarget, 0, token);
        if (external.Failed) {
            if (external.Error != null) {
                throw external.Error;
            }
            return;
        }

        CavernizeTrack effective = external.IntermediateTrack ?? rawTrueHDTarget;
        AttachToListener(effective);
        blockSize = CavernizeOutput.GetBlockSize(RenderTarget, environment);
        AudioWriter writer = !string.IsNullOrEmpty(finalName)
            ? CavernizeOutput.CreateRenderOutput(this, finalName, environment, effective, codec, bits)
            : null;
        ExecuteRenderBody(effective, writer, finalName, token);
    }

    void TranscodeDeferredTrueHDTask(CavernizeTrack rawTrueHDTarget, string path, Codec codec, BitDepth bits, CancellationToken token) {
        int keepFirst = codec == Codec.ADM_BWF || codec == Codec.ADM_BWF_Atmos ? 10 : 0;
        using ExternalConverterHandler external = CreateExternalHandler(rawTrueHDTarget, keepFirst, token);
        if (external.Failed) {
            if (external.Error != null) {
                throw external.Error;
            }
            return;
        }

        CavernizeTrack effective = external.IntermediateTrack ?? rawTrueHDTarget;
        AttachToListener(effective);
        EnvironmentWriter transcoder = EnvironmentWriter.Create(path, codec, environment.Listener, effective.Length, bits, effective.Renderer);
        ExecuteTranscodeBody(effective, transcoder, path, token);
    }

    void RenderTask(CavernizeTrack target, AudioWriter writer, string finalName, CancellationToken token) {
        using ExternalConverterHandler external = CreateExternalHandler(target, 0, token);
        if (external.Failed) {
            writer?.Dispose();
            if (external.Error != null) {
                throw external.Error;
            }
            return;
        }
        if (!external.ExternalConverterNeeded && target != GetActiveTrack()) {
            AttachToListener(target);
        }
        ExecuteRenderBody(target, writer, finalName, token);
    }

    void ExecuteRenderBody(CavernizeTrack target, AudioWriter writer, string finalName, CancellationToken token) {
        RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
        RenderStats stats = WriteRender(target, writer, RenderTarget, token);
        report.Generate(stats);

        if (writer is RIFFWaveWriter && !string.IsNullOrEmpty(finalName) && !finalName.EndsWith(waveExtension, StringComparison.OrdinalIgnoreCase)) {
            RunOnUiThread(() => SetConvertStatus(S["FinalizingFormat"], 0.96));
            string exportedAudio = Path.ChangeExtension(finalName, waveExtension);
            string ffCodec = ExportFormat.Codec == Codec.PCM_LE && store.Force24Bit ? "pcm_s24le" : ExportFormat.FFName;
            MergeToContainer merger = new(LoadedFile.Path, exportedAudio, ffCodec);
            merger.AddArguments(RenderingSettings.MergeArguments);
            merger.SetTrackName(string.Format(S["RenderedTrackNameFmt"], RenderTarget.Name));
            if (writer.ChannelCount > 8) {
                merger.Allow8PlusChannels();
            }
            merger.MakeSafe(finalName);
            if (!merger.Merge(ffmpeg, finalName)) {
                string reason = string.IsNullOrWhiteSpace(merger.LastError) ? S["RenderMergeUnknownError"] : merger.LastError;
                AppLogger.Error($"RenderTask merge failed for '{finalName}': {reason}");
                throw new IOException(string.Format(S["FFmpegMergeFailedFmt"], reason));
            }
        }

        if (!string.IsNullOrEmpty(finalName) && File.Exists(finalName)) {
            try {
                ExportMetadataPackage classicMeta = BuildExportMetadataPackageForActiveTrack(target);
                ExportMetadataEmbedder.EmbedInPlace(finalName, classicMeta);
            } catch (Exception metaEx) {
                AppLogger.Warn($"Classic export metadata embed skipped: {metaEx.Message}");
            }
            ExportPublishResult pub = AndroidFileHelper.PublishExportedAudio(this, finalName, GetSelectedExportDestination());
            string displayPath = !string.IsNullOrEmpty(pub?.LocalPath) ? pub.LocalPath : finalName;
            lastExportSourcePath = finalName;
            AppLogger.Info($"Render finished successfully: '{displayPath}'");
            RunOnUiThread(() => {
                SetConvertStatus(string.Format(S["ExportDone"], Path.GetFileName(displayPath)), 1);
                ShowPublishedExportActions(pub, displayPath);
            });
        } else {
            RunOnUiThread(() => SetConvertStatus(S["ReportDone"], 1));
        }
    }

    void TranscodeTask(CavernizeTrack target, EnvironmentWriter writer, string finalName, CancellationToken token) {
        if (writer is DolbyAtmosBWFWriter bwfWriter) {
            bwfWriter.ExtendWithMuteTarget();
        }
        using ExternalConverterHandler external = CreateExternalHandler(target, writer is DolbyAtmosBWFWriter ? 10 : 0, token);
        if (external.Failed) {
            writer.Dispose();
            if (external.Error != null) {
                throw external.Error;
            }
            return;
        }
        ExecuteTranscodeBody(target, writer, finalName, token);
    }

    void ExecuteTranscodeBody(CavernizeTrack target, EnvironmentWriter writer, string finalName, CancellationToken token) {
        RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
        RenderStats stats = writer is BroadcastWaveFormatWriter bwf
            ? WriteTranscodeBwf(target, bwf, token)
            : WriteTranscodeGeneric(target, writer, token);
        report.Generate(stats);
        string doneLabel = !string.IsNullOrEmpty(finalName)
            ? string.Format(S["ExportDone"], finalName)
            : S["ReportDone"];
        RunOnUiThread(() => SetConvertStatus(doneLabel, 1f));
    }

    ExternalConverterHandler CreateExternalHandler(CavernizeTrack target, int keepFirstSources, CancellationToken token = default) {
        AndroidLicencePrompt prompt = new((desc, text) => {
            if (store.TruehddLicenceAccepted) {
                return true;
            }
            bool accepted = false;
            using ManualResetEventSlim mre = new(false);
            RunOnUiThread(() => {
                OneUiModalDialog.Show(
                    this,
                    S["TrueHDLicenceTitle"],
                    $"{desc}\n\n{text}",
                    icon: OneUiIconType.Sparkles,
                    iconBadgeColor: OneUiTheme.Primary,
                    positiveText: S["TrueHDDownloadDecode"],
                    onPositive: () => {
                        store.TruehddLicenceAccepted = true;
                        accepted = true;
                        mre.Set();
                    },
                    negativeText: S["CancelBtn"],
                    onNegative: () => { accepted = false; mre.Set(); },
                    cancelable: false);
            });
            mre.Wait();
            return accepted;
        });

        ExternalConverterHandler external = new(target, prompt,
            p => RunOnUiThread(() => SetConvertStatus(convertStatusText.Text, p)),
            msg => RunOnUiThread(() => SetConvertStatus(msg, convertProgressBar.Progress / 1000.0)),
            RunOnUiThreadSync,
            token);
        if (!external.Failed && external.ExternalConverterNeeded && target.Codec == Codec.TrueHD) {
            // Attachment for deferred TrueHD happens in RenderDeferredTrueHDTask / TranscodeDeferredTrueHDTask
        } else if (!external.Failed) {
            external.Attach(environment.Listener, upmixingSettings, keepFirstSources);
        }
        return external;
    }

    RenderStats WriteRender(CavernizeTrack target, AudioWriter writer, RenderTarget renderTarget, CancellationToken token) {
        RenderStats stats = store.QualityGrading ? new RenderStatsEx(environment.Listener) : new RenderStats(environment.Listener);
        long rendered = 0;
        long totalLength = target.Length;
        int updateRate = environment.Listener.UpdateRate;
        DateTime start = DateTime.Now;
        long nextUiSample = 0;

        VirtualizerFilter virtualizer = null;
        Normalizer normalizer = null;
        bool virtualizerState = Listener.HeadphoneVirtualizer;
        float prevListenerNormalizer = environment.Listener.Normalizer;

        if (virtualizerState || store.SpeakerVirtualizer) {
            environment.Listener.BypassInternalVirtualizer = true;
            virtualizer = new VirtualizerFilter();
            virtualizer.SetLayout();
            environment.Listener.Normalizer = 0f;
            normalizer = new Normalizer(true) {
                SampleRate = environment.Listener.SampleRate,
                Channels = Listener.Channels.Length
            };
        }

        int cachePosition = 0;
        float[] writeCache = new float[blockSize / renderTarget.OutputChannels * Listener.Channels.Length];
        bool customPlayerMute = applyRoomToExportCheck != null && applyRoomToExportCheck.Checked;
        bool anySolo = customPlayerMute && trackSourceItems.Any(x => x.IsSolo);

        try {
            while (rendered < totalLength) {
                token.ThrowIfCancellationRequested();

                IReadOnlyList<Source> objects = target.Renderer.Objects;
                for (int i = 0; i < objects.Count; i++) {
                    bool mute = false;
                    Vector3 rawPos = objects[i].Position / Listener.EnvironmentSize;
                    if (RenderingSettings.MuteBed && MathF.Abs(rawPos.X) % 1 < .01f &&
                        MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z) % 1 < .01f) {
                        mute = true;
                    }
                    if (RenderingSettings.MuteGround && MathF.Abs(rawPos.Y) < .05f) {
                        mute = true;
                    }
                    if (customPlayerMute && i < trackSourceItems.Count) {
                        if (trackSourceItems[i].IsMuted || (anySolo && !trackSourceItems[i].IsSolo)) {
                            mute = true;
                        }
                    }
                    objects[i].Mute = mute;
                }

                float[] result;
                try {
                    result = environment.Listener.Render();
                } catch {
                    result = new float[Listener.Channels.Length * updateRate];
                }

                bool flush = false;
                if (totalLength - rendered < updateRate) {
                    Array.Resize(ref result, (int)((totalLength - rendered) * Listener.Channels.Length));
                    flush = true;
                }

                Array.Copy(result, 0, writeCache, cachePosition, result.Length);
                cachePosition += result.Length;
                if (cachePosition == writeCache.Length || flush) {
                    if (flush && cachePosition < writeCache.Length) {
                        Array.Clear(writeCache, cachePosition, writeCache.Length - cachePosition);
                    }
                    if (virtualizer == null) {
                        if (renderTarget is not DownmixedRenderTarget downmix) {
                            writer?.WriteBlock(writeCache, 0, cachePosition);
                        } else {
                            downmix.PerformMerge(writeCache);
                            writer?.WriteChannelLimitedBlock(writeCache, downmix.OutputChannels, Listener.Channels.Length, 0, cachePosition);
                        }
                    } else {
                        if (cachePosition == writeCache.Length) {
                            virtualizer.Process(writeCache, environment.Listener.SampleRate);
                            normalizer.Process(writeCache);
                            writer?.WriteChannelLimitedBlock(writeCache, renderTarget.OutputChannels, Listener.Channels.Length, 0, cachePosition);
                        } else {
                            float[] tailBlock = new float[cachePosition];
                            Array.Copy(writeCache, 0, tailBlock, 0, cachePosition);
                            virtualizer.Process(tailBlock, environment.Listener.SampleRate);
                            normalizer.Process(tailBlock);
                            writer?.WriteChannelLimitedBlock(tailBlock, renderTarget.OutputChannels, Listener.Channels.Length, 0, cachePosition);
                        }
                    }
                    cachePosition = 0;
                }

                if (rendered > secondFrame) {
                    stats.Update(result);
                }

                rendered += updateRate;
                if (rendered >= nextUiSample || rendered >= totalLength) {
                    double progress = Math.Clamp(rendered / (double)totalLength, 0, 1);
                    TimeSpan elapsed = DateTime.Now - start;
                    double speed = elapsed.TotalSeconds > 0 ? (rendered / (double)environment.Listener.SampleRate) / elapsed.TotalSeconds : 0;
                    RunOnUiThread(() => SetConvertStatus(string.Format(S["RenderProgressSpeedFmt"], progress, speed), progress));
                    nextUiSample = rendered + 50000;
                }
            }
        } finally {
            writer?.Dispose();
            environment.Listener.BypassInternalVirtualizer = false;
            environment.Listener.Normalizer = prevListenerNormalizer;
            if (virtualizerState) {
                Listener.HeadphoneVirtualizer = true;
            }
        }
        return stats;
    }

    RenderStats WriteTranscodeGeneric(CavernizeTrack target, EnvironmentWriter writer, CancellationToken token) {
        RenderStats stats = new(environment.Listener);
        long rendered = 0;
        long total = target.Length;
        long nextUi = 0;
        while (rendered < total) {
            token.ThrowIfCancellationRequested();
            writer.WriteNextFrame();
            rendered += environment.Listener.UpdateRate;
            if (rendered >= nextUi || rendered >= total) {
                double p = Math.Clamp(rendered / (double)total, 0, 1);
                RunOnUiThread(() => SetConvertStatus(string.Format(S["TranscodeProgressFmt"], p), p));
                nextUi = rendered + 50000;
            }
        }
        writer.Dispose();
        return stats;
    }

    RenderStats WriteTranscodeBwf(CavernizeTrack target, BroadcastWaveFormatWriter writer, CancellationToken token) {
        RenderStats stats = new(environment.Listener);
        long rendered = 0;
        long total = target.Length;
        long nextUi = 0;
        while (rendered < total) {
            token.ThrowIfCancellationRequested();
            writer.WriteNextFrame();
            rendered += environment.Listener.UpdateRate;
            if (rendered >= nextUi || rendered >= total) {
                double p = Math.Clamp(rendered / (double)total * progressSplit, 0, progressSplit);
                RunOnUiThread(() => SetConvertStatus(string.Format(S["TranscodeAdmProgressFmt"], p), p));
                nextUi = rendered + 50000;
            }
        }
        writer.FinalFeedback = p => RunOnUiThread(() => SetConvertStatus(
            string.Format(S["FinalizingAdmProgressFmt"], p),
            progressSplit + p * (1 - progressSplit)));
        writer.FinalFeedbackStart = progressSplit;
        writer.Dispose();
        return stats;
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
        if (studioWorkflowSelector != null) {
            studioWorkflowSelector.Enabled = !running;
            studioWorkflowSelector.Alpha = running ? 0.55f : 1f;
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
