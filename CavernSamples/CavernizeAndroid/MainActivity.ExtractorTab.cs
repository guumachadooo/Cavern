using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.Format;

using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    int selectedExtractMode;
    int selectedExtractBitDepth = 1; // 0 = 16-bit, 1 = 24-bit, 2 = 32-bit float
    readonly List<(int modeIdx, LinearLayout row, View radioDot, TextView title, TextView subtitle)> extractModeRows = [];
    readonly List<Button> extractBitButtons = [];

    CheckBox extractJsonCheck, extractResidualCheck;
    LinearLayout extractBitSection, extractJsonCheckWrap, extractResidualCheckWrap, extractSelectionSection;
    LinearLayout extractAiChainCard;
    TextView extractAiChainSummaryText;
    Button extractBadgeOnnxBtn, extractBadgeTwinBtn;
    LinearLayout extractorChecklistContainer, extractedFilesContainer;
    Button extractBtn, cancelExtractBtn;
    ProgressBar extractProgressBar;
    TextView extractStatusText;
    readonly List<(int index, CheckBox box)> extractCheckboxes = [];
    bool pendingAutoResumeExtractAfterTrueHD;
    CavernizeTrack pendingTrueHDExtractTrack;

    LinearLayout BuildExtractorTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        ApplyTabPageCutoutPadding(page);

        // 0. Active File Source Bar (Clean workspace context & track selector)
        page.AddView(CreateWorkspaceSourceBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout card = Card(S["ExtractTitle"], OneUiIconType.Scissors, OneUiTheme.IconStemsCalib);
        TextView desc = Label(S["ExtractDesc"], 12f, OneUiTheme.TextSub);
        desc.SetPadding(0, 0, 0, Dp(10));
        card.AddView(desc);

        // One UI 9 Grouped Goal Cards for Extraction Modes
        LinearLayout modeHeaderRow = HRow();
        modeHeaderRow.AddView(Label(S["ExtractMode"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        modeHeaderRow.AddView(CreateInlineHelpBtn(S["ExtractMode"].TrimEnd(':'), S["TipExtractModesAll"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        card.AddView(modeHeaderRow);

        extractModeRows.Clear();
        (string goalHeader, (int modeIdx, string title, string sub)[] items)[] goalGroups = [
            (S["ExtractGoalRawTitle"], [
                (0, S["ModeRawStems"], S["ModeRawStemsSub"]),
                (3, S["ModeRawBitstreamClean"], S["ModeRawBitstreamSub"])
            ]),
            (S["ExtractGoalSpatialTitle"], [
                (1, S["ModeSpatialEach"], S["ModeSpatialEachSub"]),
                (2, S["Mode3DCombined"], S["Mode3DCombinedSub"])
            ]),
            (S["ExtractGoalNeuralTitle"], [
                (4, S["ModeNeuralStemsClean"], S["ModeNeuralStemsSub"])
            ]),
            (S["ExtractGoalRestoreTitle"], [
                (5, S["ModeNeuralRestorerClean"], S["ModeNeuralRestorerSub"]),
                (6, S["ModeLgsrHybridStemsClean"], S["ModeLgsrHybridStemsSub"])
            ])
        ];

        for (int g = 0; g < goalGroups.Length; g++) {
            var (goalHeader, items) = goalGroups[g];
            LinearLayout goalCard = new(this) { Orientation = Orientation.Vertical };
            goalCard.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 20f);
            goalCard.SetPadding(Dp(12), Dp(8), Dp(12), Dp(6));

            TextView goalHdrTv = Label(goalHeader, 11.3f, OneUiTheme.PeachText);
            goalHdrTv.Typeface = OneUiTheme.GetAppleFontBold(this);
            goalHdrTv.SetPadding(Dp(2), 0, Dp(2), Dp(4));
            goalCard.AddView(goalHdrTv);

            for (int i = 0; i < items.Length; i++) {
                var (modeIdx, modeTitle, modeSub) = items[i];
                if (i > 0) {
                    AddDivider(goalCard);
                }
                LinearLayout row = HRow();
                row.SetPadding(Dp(4), Dp(8), Dp(4), Dp(8));
                OneUiTheme.AttachRecoil(row, 0.98f);

                View radioDot = new(this);
                LinearLayout textCol = new(this) { Orientation = Orientation.Vertical };
                TextView modeTv = Label(modeTitle, 12.8f, ColText);
                modeTv.SetIncludeFontPadding(false);
                TextView subTv = Label(modeSub, 11.1f, OneUiTheme.TextSub);
                textCol.AddView(modeTv);
                textCol.AddView(subTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(2)
                });

                row.AddView(radioDot, new LinearLayout.LayoutParams(Dp(18), Dp(18)) { RightMargin = Dp(10) });
                row.AddView(textCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                int capturedIdx = modeIdx;
                row.Click += (_, _) => {
                    if (isOperationRunning || isPreparingTrueHD) {
                        return;
                    }
                    selectedExtractMode = capturedIdx;
                    RefreshExtractModeUi();
                };
                extractModeRows.Add((modeIdx, row, radioDot, modeTv, subTv));
                goalCard.AddView(row);
            }

            card.AddView(goalCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(6),
                BottomMargin = g == goalGroups.Length - 1 ? Dp(10) : Dp(2)
            });
        }

        // Live Slot 7 AI Processing Chain Preview Card (with ONNX & Lossless Twin Readiness Badges)
        extractAiChainCard = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        extractAiChainCard.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            OneUiTheme.WithAlpha(OneUiTheme.IconAtmosUpmix, 28),
            18f,
            OneUiTheme.WithAlpha(OneUiTheme.IconAtmosUpmix, 110),
            1f);
        extractAiChainCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        TextView aiCardTitle = Label(S["ExtractAiChainCardTitle"], 11.8f, OneUiTheme.PeachText);
        aiCardTitle.Typeface = OneUiTheme.GetAppleFontBold(this);
        extractAiChainCard.AddView(aiCardTitle);

        extractAiChainSummaryText = Label("", 11.4f, ColText);
        extractAiChainSummaryText.SetPadding(0, Dp(4), 0, Dp(8));
        extractAiChainCard.AddView(extractAiChainSummaryText);

        LinearLayout aiBadgeRow = HRow();
        extractBadgeOnnxBtn = PillButton(S["StudioBadgeOnnxReady"], OnOnnxReadinessBadgeClicked);
        extractBadgeTwinBtn = PillButton(S["StudioBadgeTwinNone"], OnTwinReadinessBadgeClicked);
        extractBadgeOnnxBtn.TextSize = 11f;
        extractBadgeTwinBtn.TextSize = 11f;
        aiBadgeRow.AddView(extractBadgeOnnxBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        aiBadgeRow.AddView(extractBadgeTwinBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        extractAiChainCard.AddView(aiBadgeRow);

        card.AddView(extractAiChainCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });

        // One UI 9 Segmented Capsule Pills for Bit Depth (hidden/disabled in Mode 3 Raw Bitstream)
        extractBitSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        extractBitSection.AddView(Label(S["BitDepthLabel"], 12f, ColMuted));
        LinearLayout bitRow = HRow();
        bitRow.SetPadding(0, Dp(6), 0, Dp(8));
        extractBitButtons.Clear();
        string[] bitLabels = [S["BitDepth16Pcm"], S["BitDepth24Pcm"], S["BitDepth32Float"]];
        for (int i = 0; i < bitLabels.Length; i++) {
            int bitIdx = i;
            Button bb = PillButton(bitLabels[i], () => {
                if (isOperationRunning || isPreparingTrueHD || selectedExtractMode == 3) {
                    return;
                }
                selectedExtractBitDepth = bitIdx;
                RefreshExtractBitButtonsUi();
            });
            bb.TextSize = 11.4f;
            bb.SetMinimumWidth(0);
            bb.SetPadding(Dp(8), Dp(9), Dp(8), Dp(9));
            extractBitButtons.Add(bb);
            bitRow.AddView(bb, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                RightMargin = i < bitLabels.Length - 1 ? Dp(8) : 0
            });
        }
        extractBitSection.AddView(bitRow);
        card.AddView(extractBitSection);
        RefreshExtractBitButtonsUi();

        // Mode 0 only: 3D object trajectory metadata (.json)
        extractJsonCheck = new CheckBox(this) {
            Text = S["ExportJsonCheck"],
            Checked = true,
            TextSize = 12.5f
        };
        extractJsonCheck.SetTextColor(ColText);
        extractJsonCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
        extractJsonCheckWrap = WrapControlWithHelp(extractJsonCheck, S["ExportJsonCheck"], S["TipExtractJson"], bottomMarginDp: 4);
        card.AddView(extractJsonCheckWrap);

        // Mode 6 only: Dedicated Atmos-exclusive residual stem toggle (never hijacking extractJsonCheck!)
        extractResidualCheck = new CheckBox(this) {
            Text = S["ExportResidualTrackCheck"],
            Checked = true,
            TextSize = 12.5f
        };
        extractResidualCheck.SetTextColor(ColText);
        extractResidualCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
        extractResidualCheckWrap = WrapControlWithHelp(extractResidualCheck, S["ExportResidualTrackCheck"], S["TipExtractResidual"], bottomMarginDp: 4);
        card.AddView(extractResidualCheckWrap);

        // Modes 0..2 only: Quick selection pills & per-channel/object checklist
        extractSelectionSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        AddDivider(extractSelectionSection);
        HorizontalScrollView selScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout selRow = HRow();
        selRow.SetPadding(0, Dp(8), 0, Dp(8));
        selRow.AddView(QuickSelBtn(S["SelectAll"], () => SetExtractSelection(_ => true)));
        selRow.AddView(QuickSelBtn(S["Select3D"], item => item.IsDynamicObject));
        selRow.AddView(QuickSelBtn(S["SelectBeds"], item => item.IsBed));
        selRow.AddView(QuickSelBtn(S["InvertSel"], () => {
            foreach (var (_, box) in extractCheckboxes) {
                box.Checked = !box.Checked;
            }
        }));
        selScroll.AddView(selRow);
        extractSelectionSection.AddView(selScroll);

        extractorChecklistContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        extractSelectionSection.AddView(extractorChecklistContainer);
        card.AddView(extractSelectionSection);

        LinearLayout actionRow = HRow();
        actionRow.SetPadding(0, Dp(12), 0, Dp(8));
        extractBtn = PrimaryButton(S["ExtractBtn"], StartExtraction);
        cancelExtractBtn = PillButton(S["CancelBtn"], () => activeCts?.Cancel());
        cancelExtractBtn.Enabled = false;
        actionRow.AddView(extractBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2.2f) { RightMargin = Dp(8) });
        actionRow.AddView(cancelExtractBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(actionRow);

        extractProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0
        };
        OneUiTheme.StyleProgressBar(this, extractProgressBar);
        card.AddView(extractProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)) { TopMargin = Dp(4) });

        extractStatusText = Label(S["Ready"], 12f, OneUiTheme.IconSystemInfo);
        extractStatusText.SetPadding(0, Dp(6), 0, Dp(4));
        card.AddView(extractStatusText);

        page.AddView(card);

        LinearLayout outCard = Card(S["ExtractOutputCardTitle"], OneUiIconType.Folder, OneUiTheme.IconStudioExport);
        extractedFilesContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        outCard.AddView(extractedFilesContainer);
        page.AddView(outCard);

        RefreshExtractModeUi();
        RefreshExtractorChecklist();
        PopulateExtractedFiles(null);
        return page;
    }

    void RefreshExtractModeUi() {
        for (int i = 0; i < extractModeRows.Count; i++) {
            var (modeIdx, row, dot, title, subtitle) = extractModeRows[i];
            bool sel = modeIdx == selectedExtractMode;
            row.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 56), 14f)
                : null;
            dot.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp, ColText, 3f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp, OneUiTheme.TextMuted, 1.5f);
            title.SetTypeface(null, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            title.SetTextColor(sel ? ColText : OneUiTheme.TextSecondary);
            subtitle.SetTextColor(sel ? OneUiTheme.TextSecondary : OneUiTheme.TextMuted);
        }

        bool isRawBitstream = selectedExtractMode == 3;
        if (extractBitSection != null) {
            extractBitSection.Visibility = isRawBitstream ? ViewStates.Gone : ViewStates.Visible;
        }
        for (int i = 0; i < extractBitButtons.Count; i++) {
            extractBitButtons[i].Enabled = !isOperationRunning && !isRawBitstream;
        }

        if (extractJsonCheckWrap != null) {
            extractJsonCheckWrap.Visibility = selectedExtractMode == 0 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (extractResidualCheckWrap != null) {
            extractResidualCheckWrap.Visibility = selectedExtractMode == 6 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (extractSelectionSection != null) {
            extractSelectionSection.Visibility = selectedExtractMode <= 2 ? ViewStates.Visible : ViewStates.Gone;
        }

        RefreshExtractAiChainPreviewUi();
    }

    void RefreshExtractAiChainPreviewUi() {
        if (uiDestroyed || extractAiChainCard == null || store == null) {
            return;
        }
        int mode = selectedExtractMode;
        bool isAiMode = mode >= 4;
        extractAiChainCard.Visibility = isAiMode ? ViewStates.Visible : ViewStates.Gone;
        if (!isAiMode) {
            return;
        }

        Cavernize.Logic.Spatial.NeuralAudioConfig neuralCfg = store.BuildNeuralAudioConfig();
        bool neuralKaraokeAtmos = mode == 4 && neuralCfg.SpatialKaraokeEnabled && trackSourceItems.Count > 2;
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal = mode == 5
            ? Cavernize.Logic.Spatial.NeuralPipelineGoal.LosslessResidualRestore
            : (neuralKaraokeAtmos
                ? Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke
                : Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation);
        Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(goal, neuralCfg);
        string modelId = mode == 5
            ? neuralCfg.ActiveRestorerModelId
            : (neuralKaraokeAtmos ? neuralCfg.ActiveKaraokeModelId : neuralCfg.ActiveStemModelId);
        string modelPath = OnnxRuntimeInstallerService.ResolveModelPath(this, modelId);
        bool runtimeReady = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
        bool modelReady = runtimeReady && ((plan != null && plan.IsRunnable) || !string.IsNullOrEmpty(modelPath));

        if (extractAiChainSummaryText != null) {
            if (plan != null && !string.IsNullOrEmpty(plan.Summary) && plan.IsRunnable) {
                extractAiChainSummaryText.Text = string.Format(S["NeuralPlanPreviewFmt"], plan.Summary);
                extractAiChainSummaryText.SetTextColor(OneUiTheme.IconSystemInfo);
            } else if (!string.IsNullOrEmpty(modelPath)) {
                extractAiChainSummaryText.Text = string.Format(S["NeuralPlanPreviewFmt"], Path.GetFileNameWithoutExtension(modelPath));
                extractAiChainSummaryText.SetTextColor(OneUiTheme.IconSystemInfo);
            } else {
                extractAiChainSummaryText.Text = S["ExtractAiChainNoModel"];
                extractAiChainSummaryText.SetTextColor(OneUiTheme.PeachText);
            }
        }

        StyleReadinessBadge(
            extractBadgeOnnxBtn,
            modelReady ? S["StudioBadgeOnnxReady"] : S["StudioBadgeOnnxMissing"],
            modelReady ? OneUiTheme.FunctionalGreen : OneUiTheme.FunctionalOrange,
            !modelReady);

        if (extractBadgeTwinBtn != null) {
            extractBadgeTwinBtn.Visibility = (mode == 4 || mode == 6) ? ViewStates.Visible : ViewStates.Gone;
            bool twinSynced = IsLosslessTwinSynced(out _);
            Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
            if (twinSynced) {
                StyleReadinessBadge(extractBadgeTwinBtn, S["StudioBadgeTwinSynced"], OneUiTheme.FunctionalGreen, true);
            } else if (lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless && mode != 6) {
                StyleReadinessBadge(extractBadgeTwinBtn, S["StudioBadgeTwinNative"], OneUiTheme.IconSystemInfo, false);
            } else {
                StyleReadinessBadge(extractBadgeTwinBtn, S["StudioBadgeTwinNone"], OneUiTheme.PeachText, mode == 6);
            }
        }
    }

    void RefreshExtractBitButtonsUi() {
        for (int i = 0; i < extractBitButtons.Count; i++) {
            bool sel = i == selectedExtractBitDepth;
            Button b = extractBitButtons[i];
            b.Background = sel
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
    }

    Button QuickSelBtn(string text, Func<TrackSourceItem, bool> predicate) {
        return QuickSelBtn(text, () => SetExtractSelection(predicate));
    }

    Button QuickSelBtn(string text, Action onClick) {
        Button b = PillButton(text, onClick);
        b.TextSize = 11.5f;
        b.SetPadding(Dp(13), Dp(7), Dp(13), Dp(7));
        b.SetMinimumHeight(Dp(36));
        b.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) };
        return b;
    }

    void SetExtractSelection(Func<TrackSourceItem, bool> predicate) {
        foreach (var (idx, box) in extractCheckboxes) {
            if (idx >= 0 && idx < trackSourceItems.Count) {
                box.Checked = predicate(trackSourceItems[idx]);
            }
        }
    }

    void RefreshExtractorChecklist() {
        if (extractorChecklistContainer == null) {
            return;
        }
        extractorChecklistContainer.RemoveAllViews();
        extractCheckboxes.Clear();

        if (trackSourceItems.Count == 0) {
            extractorChecklistContainer.AddView(Label(S["NoTrack"], 12f, ColMuted));
        } else {
            foreach (TrackSourceItem item in trackSourceItems) {
                CheckBox cb = new(this) {
                    Text = item.Name,
                    Checked = true,
                    TextSize = 12.5f
                };
                cb.SetTextColor(item.IsDynamicObject ? OneUiTheme.DynamicObject : ColText);
                cb.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(item.IsDynamicObject ? OneUiTheme.DynamicObject : ColAccent);
                extractorChecklistContainer.AddView(cb);
                extractCheckboxes.Add((item.Index, cb));
            }
        }

        RefreshExtractAiChainPreviewUi();
        if (extractedFilesContainer != null && !isOperationRunning && extractedFilesContainer.ChildCount <= 1) {
            PopulateExtractedFiles(null);
        }

        if (!isPreparingTrueHD && pendingAutoResumeExtractAfterTrueHD) {
            CavernizeTrack expected = pendingTrueHDExtractTrack;
            pendingAutoResumeExtractAfterTrueHD = false;
            pendingTrueHDExtractTrack = null;
            if (decodedTrueHDTrack != null && selectedTrack == expected && !isOperationRunning) {
                uiHandler?.Post(StartExtraction);
            }
        }
    }

    void StartExtraction() {
        if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
            ShowError(S["OpRunning"]);
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        int mode = selectedExtractMode;
        if (active.Codec == Cavern.Format.Common.Codec.TrueHD && mode != 3) {
            pendingTrueHDExtractTrack = selectedTrack;
            pendingAutoResumeExtractAfterTrueHD = true;
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }

        StopPlayback();
        BitDepth bits = selectedExtractBitDepth switch {
            0 => BitDepth.Int16,
            2 => BitDepth.Float32,
            _ => BitDepth.Int24
        };
        bool exportJson = extractJsonCheck != null && extractJsonCheck.Checked;
        bool exportResidual = extractResidualCheck != null && extractResidualCheck.Checked;
        List<int> selectedIndices = extractCheckboxes.Where(x => x.box.Checked).Select(x => x.index).ToList();

        if (mode <= 2 && selectedIndices.Count == 0) {
            ShowError(S["ExtractSelectAtLeastOne"]);
            return;
        }

        Cavernize.Logic.Spatial.NeuralAudioConfig neuralCfg = store.BuildNeuralAudioConfig();
        Cavernize.Logic.Spatial.LgsrConfig lgsrCfg = store.BuildLgsrConfig();
        bool neuralKaraokeAtmos = mode == 4 && neuralCfg.SpatialKaraokeEnabled && trackSourceItems.Count > 2;
        Cavernize.Logic.Spatial.NeuralPipelineGoal neuralGoal = mode == 5
            ? Cavernize.Logic.Spatial.NeuralPipelineGoal.LosslessResidualRestore
            : (neuralKaraokeAtmos
                ? Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke
                : Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation);
        Cavernize.Logic.Spatial.NeuralExecutionPlan neuralPlan = neuralOrchestrator?.BuildPlan(neuralGoal, neuralCfg);
        string neuralModelId = mode == 5
            ? neuralCfg.ActiveRestorerModelId
            : (neuralKaraokeAtmos ? neuralCfg.ActiveKaraokeModelId : neuralCfg.ActiveStemModelId);
        string neuralModelPath = OnnxRuntimeInstallerService.ResolveModelPath(this, neuralModelId);
        Cavernize.Logic.Spatial.NeuralModelCatalogEntry neuralModelEntry =
            OnnxRuntimeInstallerService.GetAllRegisteredModels(this)
                .FirstOrDefault(m => string.Equals(m.Id, neuralModelId, StringComparison.OrdinalIgnoreCase))
            ?? Cavernize.Logic.Spatial.NeuralModelCatalog.FindById(neuralModelId);
        string tier3ModelPath = OnnxRuntimeInstallerService.ResolveModelPath(this, neuralCfg.ActiveRestorerModelId);
        Cavernize.Logic.Spatial.NeuralModelCatalogEntry tier3ModelEntry =
            OnnxRuntimeInstallerService.GetAllRegisteredModels(this)
                .FirstOrDefault(m => string.Equals(m.Id, neuralCfg.ActiveRestorerModelId, StringComparison.OrdinalIgnoreCase))
            ?? Cavernize.Logic.Spatial.NeuralModelCatalog.FindById(neuralCfg.ActiveRestorerModelId);

        if ((mode == 4 && !neuralKaraokeAtmos) || mode == 5 || mode == 6) {
            if (!OnnxRuntimeInstallerService.IsRuntimeInstalled(this)) {
                ShowError(S["NeuralRuntimeNeedInstall"]);
                return;
            }
            if ((neuralPlan == null || !neuralPlan.IsRunnable) && string.IsNullOrEmpty(neuralModelPath)) {
                ShowError(S["NeuralModelNeedInstall"]);
                return;
            }
        }
        var twinProvider = activeLosslessTwin ?? playerEngine?.SceneHub?.ActiveLosslessReference as Cavernize.Logic.Spatial.UniversalLosslessTwinProvider;
        if (mode == 6 && (twinProvider == null || !twinProvider.HasSyncedLosslessTwin)) {
            ShowError(S["LgsrMasterNeedsTwin"]);
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(LoadedFile?.Path ?? "track");
        string folderTag = $"{baseName}_{DateTime.Now:HHmmss}";
        string relSubfolder = Path.Combine("Stems", folderTag);
        string outDir = AndroidFileHelper.GetWorkingOutputDir(this, relSubfolder);
        AppLogger.Info($"Starting stem extraction: mode={mode}, count={selectedIndices.Count}, out='{outDir}'");

        activeCts?.Dispose();
        activeCts = new CancellationTokenSource();
        CancellationToken token = activeCts.Token;

        isOperationRunning = true;
        SetRunningUiState(true);
        extractProgressBar.Indeterminate = false;
        extractProgressBar.Progress = 0;
        extractStatusText.Text = S["Starting"];
        extractedFilesContainer.RemoveAllViews();

        Task.Run(() => {
            AndroidRuntimeTelemetry.ApplyBackgroundThreadQoS();
            try {
                store.ApplyGlobals();
                List<string> created = [];
                void ReportProg(double p, string msg) => RunOnUiThread(() => {
                    extractProgressBar.Indeterminate = false;
                    extractProgressBar.Progress = Math.Clamp((int)Math.Round(p * 1000.0), 0, 1000);
                    extractStatusText.Text = msg;
                });

                if (mode == 0) {
                    StemExtractionResult res = StemExtractor.ExtractRawStems(
                        active, outDir, bits, selectedIndices, exportJson, ReportProg, token);
                    created.AddRange(res.CreatedFiles);
                    if (!string.IsNullOrEmpty(res.TrajectoryJsonPath)) {
                        created.Add(res.TrajectoryJsonPath);
                    }
                } else if (mode == 1 || mode == 2) {
                    bool combine = mode == 2;
                    StemExtractionResult res = StemExtractor.ExtractSpatialStems(
                        this, active, outDir, bits, selectedIndices, combine,
                        playerEngine.ListenerPosition, playerEngine.ListenerRotation,
                        playerEngine.DistanceSimulation, playerEngine.VolumeRolloff,
                        ReportProg, token);
                    created.AddRange(res.CreatedFiles);
                } else if (mode == 3) {
                    Cavern.Format.Common.Codec rawCodec = active.SourceCodec != Cavern.Format.Common.Codec.Unknown
                        ? active.SourceCodec
                        : active.Codec;
                    string rawExt = rawCodec switch {
                        Cavern.Format.Common.Codec.EnhancedAC3 => ".eac3",
                        Cavern.Format.Common.Codec.AC3 => ".ac3",
                        Cavern.Format.Common.Codec.TrueHD => ".thd",
                        _ => ".bin"
                    };
                    string rawPath = Path.Combine(outDir, baseName + rawExt);
                    StemExtractor.ExtractRawBitstream(active, rawPath, ReportProg, token);
                    created.Add(rawPath);
                } else if (mode == 5) {
                    StemExtractionResult res = StemExtractor.ExtractNeuralStems(
                        active,
                        neuralRuntime,
                        neuralModelPath,
                        neuralModelEntry,
                        outDir,
                        bits,
                        neuralCfg,
                        exportSpatialKaraokeAtmos: false,
                        exportHybrid714: false,
                        onProgress: ReportProg,
                        token: token,
                        losslessTwin: twinProvider,
                        orchestrator: neuralOrchestrator,
                        onSeparatedTrackReady: null,
                        runStandaloneRestorer: true);
                    created.AddRange(res.CreatedFiles);
                } else if (mode == 6) {
                    StemExtractionResult res = StemExtractor.ExtractLosslessHybridSpatialMaster(
                        active,
                        twinProvider,
                        neuralRuntime,
                        neuralModelPath,
                        neuralModelEntry,
                        outDir,
                        bits,
                        neuralCfg,
                        lgsrCfg,
                        tier3ModelPath,
                        tier3ModelEntry,
                        exportResidualTrack: exportResidual,
                        onProgress: ReportProg,
                        token: token,
                        orchestrator: neuralOrchestrator,
                        onSeparatedTrackReady: separated => {
                            if (separated != null) {
                                playerEngine?.BindSeparatedNeuralTrack(separated);
                                RunOnUiThread(() => {
                                    SyncNeuralRuntimeDiagnostics();
                                    RefreshNeuralAudioUi();
                                });
                            }
                        });
                    created.AddRange(res.CreatedFiles);
                } else {
                    bool exportHybrid = neuralCfg.SpatialUpmixMode == Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.HybridNeuralDirAc;
                    StemExtractionResult res = StemExtractor.ExtractNeuralStems(
                        active,
                        neuralRuntime,
                        neuralModelPath,
                        neuralModelEntry,
                        outDir,
                        bits,
                        neuralCfg,
                        exportSpatialKaraokeAtmos: neuralKaraokeAtmos,
                        exportHybrid714: exportHybrid,
                        onProgress: ReportProg,
                        token: token,
                        losslessTwin: twinProvider,
                        orchestrator: neuralOrchestrator,
                        onSeparatedTrackReady: separated => {
                            if (separated != null) {
                                playerEngine?.BindSeparatedNeuralTrack(separated);
                                RunOnUiThread(() => {
                                    SyncNeuralRuntimeDiagnostics();
                                    RefreshNeuralAudioUi();
                                });
                            }
                        });
                    created.AddRange(res.CreatedFiles);
                }

                string publishRel = $"Stems/{folderTag}";
                foreach (string f in created) {
                    AndroidFileHelper.PublishToDownloads(this, f, publishRel);
                }

                AppLogger.Info($"Stem extraction complete: {created.Count} files in '{outDir}'");
                RunOnUiThread(() => {
                    extractProgressBar.Indeterminate = false;
                    extractProgressBar.Progress = 1000;
                    extractStatusText.Text = string.Format(S["ExtractDone"], created.Count, outDir);
                    PopulateExtractedFiles(created);
                });
            } catch (OperationCanceledException) {
                RunOnUiThread(() => {
                    extractProgressBar.Indeterminate = false;
                    extractProgressBar.Progress = 0;
                    extractStatusText.Text = S["CancelledStatus"];
                    PopulateExtractedFiles(null);
                });
            } catch (Exception ex) {
                AppLogger.Error("StartExtraction", ex);
                RunOnUiThread(() => {
                    extractProgressBar.Indeterminate = false;
                    extractProgressBar.Progress = 0;
                    extractStatusText.Text = ex.Message;
                    PopulateExtractedFiles(null);
                    ShowError(ex.Message);
                });
            } finally {
                isOperationRunning = false;
                RunOnUiThread(() => SetRunningUiState(false));
            }
        });
    }

    void PopulateExtractedFiles(List<string> paths) {
        if (extractedFilesContainer == null) {
            return;
        }
        extractedFilesContainer.RemoveAllViews();
        if (paths == null || paths.Count == 0) {
            string emptyMsg = GetActiveTrack() == null
                ? S["ExtractOutputEmptyNoTrack"]
                : S["ExtractOutputEmptyReady"];
            TextView emptyTv = Label(emptyMsg, 12f, OneUiTheme.TextMuted);
            emptyTv.SetPadding(Dp(4), Dp(6), Dp(4), Dp(6));
            extractedFilesContainer.AddView(emptyTv);
            return;
        }
        foreach (string path in paths) {
            string localPath = path;
            LinearLayout row = HRow();
            row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
            row.SetPadding(Dp(12), Dp(9), Dp(10), Dp(9));
            LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            };

            TextView nameTv = Label(Path.GetFileName(localPath), 12f, ColText);
            row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            if (localPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
                LinearLayout playBtn = OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.Play, null, () => {
                    try {
                        OpenLocalPathWithFallbackAsync(localPath, switchToTab: 0);
                    } catch (Exception ex) {
                        ShowError(ex.Message);
                    }
                }, primary: true);
                row.AddView(playBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
            }

            LinearLayout shareBtn = OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.Share, null, () => ShareLocalFile(localPath));
            row.AddView(shareBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            extractedFilesContainer.AddView(row, lp);
        }
    }
}
