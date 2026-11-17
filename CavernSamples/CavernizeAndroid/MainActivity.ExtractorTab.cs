using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.Format;

using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;

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
        page.SetPadding(Dp(14), Dp(4), Dp(14), Dp(88));

        LinearLayout card = Card(S["ExtractTitle"], "✂", OneUiTheme.FunctionalIndigo);
        TextView desc = Label(S["ExtractDesc"], 12f, OneUiTheme.TextSub);
        desc.SetPadding(0, 0, 0, Dp(10));
        card.AddView(desc);

        // One UI 9 Visual Radio Rows for Extraction Modes (matching Panel Settings Screenshot 12)
        card.AddView(Label(S["ExtractMode"], 12f, ColMuted));
        LinearLayout modeGroup = new(this) { Orientation = Orientation.Vertical };
        modeGroup.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 20f);
        modeGroup.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        extractModeRows.Clear();

        string[] modes = [S["ModeRawStems"], S["ModeSpatialEach"], S["Mode3DCombined"], S["ModeRawBitstream"]];
        for (int i = 0; i < modes.Length; i++) {
            int modeIdx = i;
            if (i > 0) {
                AddDivider(modeGroup);
            }
            LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
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
        LinearLayout bitRow = new(this) { Orientation = Orientation.Horizontal };
        bitRow.SetPadding(0, Dp(6), 0, Dp(8));
        extractBitButtons.Clear();
        string[] bitLabels = ["16-bit PCM", "24-bit PCM", "32-bit Float"];
        for (int i = 0; i < bitLabels.Length; i++) {
            int bitIdx = i;
            Button bb = PillButton(bitLabels[i], () => {
                selectedExtractBitDepth = bitIdx;
                RefreshExtractBitButtonsUi();
            });
            bb.TextSize = 12f;
            extractBitButtons.Add(bb);
            bitRow.AddView(bb, new LinearLayout.LayoutParams(0, Dp(36), 1f) {
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
        extractJsonCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(OneUiTheme.CoralStart);
        card.AddView(extractJsonCheck);

        // Quick selection pills
        LinearLayout selRow = new(this) { Orientation = Orientation.Horizontal };
        selRow.SetPadding(0, Dp(8), 0, Dp(8));
        selRow.AddView(QuickSelBtn(S["SelectAll"], () => SetExtractSelection(_ => true)));
        selRow.AddView(QuickSelBtn(S["Select3D"], item => item.IsDynamicObject));
        selRow.AddView(QuickSelBtn(S["SelectBeds"], item => item.IsBed));
        selRow.AddView(QuickSelBtn(S["InvertSel"], () => {
            foreach (var (_, box) in extractCheckboxes) {
                box.Checked = !box.Checked;
            }
        }));
        card.AddView(selRow);

        extractorChecklistContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.AddView(extractorChecklistContainer);

        LinearLayout actionRow = new(this) { Orientation = Orientation.Horizontal };
        actionRow.SetPadding(0, Dp(12), 0, Dp(8));
        extractBtn = PrimaryButton(S["ExtractBtn"], StartExtraction);
        cancelExtractBtn = PillButton(S["CancelBtn"], () => activeCts?.Cancel());
        cancelExtractBtn.Enabled = false;
        actionRow.AddView(extractBtn, new LinearLayout.LayoutParams(0, Dp(44), 2.2f) { RightMargin = Dp(8) });
        actionRow.AddView(cancelExtractBtn, new LinearLayout.LayoutParams(0, Dp(44), 1f));
        card.AddView(actionRow);

        extractProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0
        };
        OneUiTheme.StyleProgressBar(this, extractProgressBar);
        card.AddView(extractProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)) { TopMargin = Dp(4) });

        extractStatusText = Label(S["Ready"], 12f, ColCyan);
        extractStatusText.SetPadding(0, Dp(6), 0, Dp(4));
        card.AddView(extractStatusText);

        page.AddView(card);

        LinearLayout outCard = Card("Arquivos Extraídos / Output", "📁", OneUiTheme.FunctionalGreen);
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
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.CoralStart, OneUiTheme.PillCornerDp, ColText, 3f)
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
        b.SetPadding(Dp(6), Dp(2), Dp(6), Dp(2));
        b.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(34), 1f) { RightMargin = Dp(4) };
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
        if (isOperationRunning) {
            ShowError(S["OpRunning"]);
            return;
        }
        if (selectedTrack == null || !selectedTrack.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        if (selectedTrack.Codec == Cavern.Format.Common.Codec.TrueHD) {
            ShowError(S["TrueHDUnsupported"]);
            return;
        }

        StopPlayback();
        int mode = selectedExtractMode;
        BitDepth bits = selectedExtractBitDepth switch {
            0 => BitDepth.Int16,
            2 => BitDepth.Float32,
            _ => BitDepth.Int24
        };
        bool exportJson = extractJsonCheck.Checked;
        List<int> selectedIndices = extractCheckboxes.Where(x => x.box.Checked).Select(x => x.index).ToList();

        if (mode != 3 && selectedIndices.Count == 0) {
            ShowError("Select at least one channel or 3D object.");
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(LoadedFile?.Path ?? "track");
        string subfolder = $"{baseName}_stems_{DateTime.Now:HHmmss}";
        string outDir = AndroidFileHelper.GetWorkingOutputDir(this, subfolder);

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
                        selectedTrack, outDir, bits, selectedIndices, exportJson, ReportProg, token);
                    created.AddRange(res.CreatedFiles);
                    if (!string.IsNullOrEmpty(res.TrajectoryJsonPath)) {
                        created.Add(res.TrajectoryJsonPath);
                    }
                } else if (mode == 1 || mode == 2) {
                    bool combine = mode == 2;
                    StemExtractionResult res = StemExtractor.ExtractSpatialStems(
                        this, selectedTrack, outDir, bits, selectedIndices, combine,
                        playerEngine.ListenerPosition, playerEngine.ListenerRotation,
                        playerEngine.DistanceSimulation, playerEngine.VolumeRolloff,
                        ReportProg, token);
                    created.AddRange(res.CreatedFiles);
                } else {
                    string rawExt = selectedTrack.Codec switch {
                        Cavern.Format.Common.Codec.EnhancedAC3 => ".eac3",
                        Cavern.Format.Common.Codec.AC3 => ".ac3",
                        Cavern.Format.Common.Codec.TrueHD => ".thd",
                        _ => ".bin"
                    };
                    string rawPath = Path.Combine(outDir, baseName + rawExt);
                    StemExtractor.ExtractRawBitstream(selectedTrack, rawPath, ReportProg, token);
                    created.Add(rawPath);
                }

                foreach (string f in created) {
                    AndroidFileHelper.PublishToDownloads(this, f, subfolder);
                }

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
            LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
            row.SetPadding(Dp(12), Dp(9), Dp(10), Dp(9));
            LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            };

            TextView nameTv = Label(Path.GetFileName(localPath), 12f, ColText);
            row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            if (localPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
                Button playBtn = PillButton("▶", () => {
                    try {
                        OpenContent(localPath);
                        SwitchTab(0);
                    } catch (Exception ex) {
                        ShowError(ex.Message);
                    }
                }, active: true);
                playBtn.SetPadding(0, 0, 0, 0);
                row.AddView(playBtn, new LinearLayout.LayoutParams(Dp(40), Dp(34)) { RightMargin = Dp(6) });
            }

            Button shareBtn = PillButton("↗", () => ShareLocalFile(localPath));
            shareBtn.SetPadding(0, 0, 0, 0);
            row.AddView(shareBtn, new LinearLayout.LayoutParams(Dp(40), Dp(34)));
            extractedFilesContainer.AddView(row, lp);
        }
    }
}
