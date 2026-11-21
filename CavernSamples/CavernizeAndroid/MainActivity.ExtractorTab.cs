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
    readonly List<(LinearLayout row, View radioDot, TextView title)> extractModeRows = [];
    readonly List<Button> extractBitButtons = [];

    CheckBox extractJsonCheck;
    LinearLayout extractorChecklistContainer, extractedFilesContainer;
    Button extractBtn, cancelExtractBtn;
    ProgressBar extractProgressBar;
    TextView extractStatusText;
    readonly List<(int index, CheckBox box)> extractCheckboxes = [];

    LinearLayout BuildExtractorTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(6), Dp(14), Dp(116));

        // 0. Active File Source Bar (Clean workspace context & track selector)
        page.AddView(CreateWorkspaceSourceBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout card = Card(S["ExtractTitle"], OneUiIconType.Scissors, OneUiTheme.IconStemsCalib);
        TextView desc = Label(S["ExtractDesc"], 12f, OneUiTheme.TextSub);
        desc.SetPadding(0, 0, 0, Dp(10));
        card.AddView(desc);

        // One UI 9 Visual Radio Rows for Extraction Modes
        LinearLayout modeHeaderRow = HRow();
        modeHeaderRow.AddView(Label(S["ExtractMode"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        modeHeaderRow.AddView(CreateInlineHelpBtn(S["ExtractMode"].TrimEnd(':'), S["TipExtractModes"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        card.AddView(modeHeaderRow);
        LinearLayout modeGroup = new(this) { Orientation = Orientation.Vertical };
        modeGroup.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 20f);
        modeGroup.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        extractModeRows.Clear();

        string[] modes = [
            S["ModeRawStems"],
            S["ModeSpatialEach"],
            S["Mode3DCombined"],
            S["ModeRawBitstream"],
            S["ModeNeuralStems"],
            S["ModeNeuralRestorer"],
            S["ModeLgsrHybridStems"]
        ];
        for (int i = 0; i < modes.Length; i++) {
            int modeIdx = i;
            if (i > 0) {
                AddDivider(modeGroup);
            }
            LinearLayout row = HRow();
            row.SetPadding(Dp(6), Dp(10), Dp(6), Dp(10));
            OneUiTheme.AttachRecoil(row, 0.98f);

            View radioDot = new(this);
            TextView modeTv = Label(modes[i], 13f, ColText);
            row.AddView(radioDot, new LinearLayout.LayoutParams(Dp(18), Dp(18)) { RightMargin = Dp(10) });
            row.AddView(modeTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            row.Click += (_, _) => {
                selectedExtractMode = modeIdx;
                RefreshExtractModeUi();
            };
            extractModeRows.Add((row, radioDot, modeTv));
            modeGroup.AddView(row);
        }
        card.AddView(modeGroup, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6),
            BottomMargin = Dp(10)
        });
        RefreshExtractModeUi();

        // One UI 9 Segmented Capsule Pills for Bit Depth
        card.AddView(Label(S["BitDepthLabel"], 12f, ColMuted));
        LinearLayout bitRow = HRow();
        bitRow.SetPadding(0, Dp(6), 0, Dp(8));
        extractBitButtons.Clear();
        string[] bitLabels = ["16-bit PCM", "24-bit PCM", "32-bit Float"];
        for (int i = 0; i < bitLabels.Length; i++) {
            int bitIdx = i;
            Button bb = PillButton(bitLabels[i], () => {
                selectedExtractBitDepth = bitIdx;
                RefreshExtractBitButtonsUi();
            });
            bb.TextSize = 11.5f;
            bb.SetPadding(Dp(6), Dp(8), Dp(6), Dp(8));
            extractBitButtons.Add(bb);
            bitRow.AddView(bb, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                RightMargin = i < bitLabels.Length - 1 ? Dp(6) : 0
            });
        }
        card.AddView(bitRow);
        RefreshExtractBitButtonsUi();

        AddDivider(card);
        extractJsonCheck = new CheckBox(this) {
            Text = S["ExportJsonCheck"],
            Checked = true,
            TextSize = 12.5f
        };
        extractJsonCheck.SetTextColor(ColText);
        extractJsonCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
        card.AddView(WrapControlWithHelp(extractJsonCheck, S["ExportJsonCheck"], S["TipExtractJson"], bottomMarginDp: 4));

        // Quick selection pills (horizontally scrollable so full labels never clip or wrap)
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
        card.AddView(selScroll);

        extractorChecklistContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.AddView(extractorChecklistContainer);

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

        RefreshExtractorChecklist();
        return page;
    }

    void RefreshExtractModeUi() {
        for (int i = 0; i < extractModeRows.Count; i++) {
            bool sel = i == selectedExtractMode;
            var (_, dot, title) = extractModeRows[i];
            dot.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp, ColText, 3f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp, OneUiTheme.TextMuted, 1.5f);
            title.SetTypeface(null, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            title.SetTextColor(sel ? ColText : OneUiTheme.TextSub);
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
            return;
        }

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

    void StartExtraction() {
        if (isOperationRunning || isPreparingTrueHD) {
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
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }

        StopPlayback();
        BitDepth bits = selectedExtractBitDepth switch {
            0 => BitDepth.Int16,
            2 => BitDepth.Float32,
            _ => BitDepth.Int24
        };
        bool exportJson = extractJsonCheck.Checked;
        List<int> selectedIndices = extractCheckboxes.Where(x => x.box.Checked).Select(x => x.index).ToList();

        if (mode != 3 && mode != 4 && mode != 5 && mode != 6 && selectedIndices.Count == 0) {
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
        if (mode == 6 && (activeLosslessTwin == null || !activeLosslessTwin.HasSyncedLosslessTwin)) {
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
        extractedFilesContainer.RemoveAllViews();

        Task.Run(() => {
            try {
                store.ApplyGlobals();
                List<string> created = [];
                void ReportProg(double p, string msg) => RunOnUiThread(() => {
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
                        losslessTwin: activeLosslessTwin,
                        orchestrator: neuralOrchestrator,
                        onSeparatedTrackReady: null,
                        runStandaloneRestorer: true);
                    created.AddRange(res.CreatedFiles);
                } else if (mode == 6) {
                    StemExtractionResult res = StemExtractor.ExtractLosslessHybridSpatialMaster(
                        active,
                        activeLosslessTwin,
                        neuralRuntime,
                        neuralModelPath,
                        neuralModelEntry,
                        outDir,
                        bits,
                        neuralCfg,
                        lgsrCfg,
                        tier3ModelPath,
                        tier3ModelEntry,
                        exportResidualTrack: exportJson,
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
                        losslessTwin: activeLosslessTwin,
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
                    extractProgressBar.Progress = 1000;
                    extractStatusText.Text = string.Format(S["ExtractDone"], created.Count, outDir);
                    PopulateExtractedFiles(created);
                });
            } catch (OperationCanceledException) {
                RunOnUiThread(() => {
                    extractProgressBar.Progress = 0;
                    extractStatusText.Text = S["CancelBtn"];
                });
            } catch (Exception ex) {
                AppLogger.Error("StartExtraction", ex);
                RunOnUiThread(() => {
                    extractStatusText.Text = ex.Message;
                    ShowError(ex.Message);
                });
            } finally {
                isOperationRunning = false;
                RunOnUiThread(() => SetRunningUiState(false));
            }
        });
    }

    void PopulateExtractedFiles(List<string> paths) {
        extractedFilesContainer.RemoveAllViews();
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
