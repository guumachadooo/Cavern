using Android.App;
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
    Spinner extractModeSpinner, extractBitsSpinner;
    CheckBox extractJsonCheck;
    LinearLayout extractorChecklistContainer, extractedFilesContainer;
    Button extractBtn, cancelExtractBtn;
    ProgressBar extractProgressBar;
    TextView extractStatusText;
    readonly List<(int index, CheckBox box)> extractCheckboxes = [];

    LinearLayout BuildExtractorTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(12), Dp(6), Dp(12), Dp(16));

        LinearLayout card = Card(S["ExtractTitle"]);
        TextView desc = Label(S["ExtractDesc"], 12f, ColMuted);
        desc.SetPadding(0, 0, 0, Dp(8));
        card.AddView(desc);
        AddDivider(card);

        card.AddView(Label(S["ExtractMode"], 12f, ColMuted));
        extractModeSpinner = new Spinner(this);
        string[] modes = [S["ModeRawStems"], S["ModeSpatialEach"], S["Mode3DCombined"], S["ModeRawBitstream"]];
        ArrayAdapter<string> modeAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, modes);
        modeAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        extractModeSpinner.Adapter = modeAdapter;
        card.AddView(extractModeSpinner);

        AddDivider(card);
        card.AddView(Label(S["BitDepthLabel"], 12f, ColMuted));
        extractBitsSpinner = new Spinner(this);
        string[] bitLabels = ["16-bit PCM", "24-bit PCM", "32-bit Float"];
        ArrayAdapter<string> bitsAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, bitLabels);
        bitsAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        extractBitsSpinner.Adapter = bitsAdapter;
        extractBitsSpinner.SetSelection(1); // 24-bit default
        card.AddView(extractBitsSpinner);

        AddDivider(card);
        extractJsonCheck = new CheckBox(this) {
            Text = S["ExportJsonCheck"],
            Checked = true,
            TextSize = 12.5f
        };
        extractJsonCheck.SetTextColor(ColText);
        extractJsonCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
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
        actionRow.SetPadding(0, Dp(10), 0, Dp(6));
        extractBtn = PrimaryButton(S["ExtractBtn"], StartExtraction);
        cancelExtractBtn = PillButton(S["CancelBtn"], () => activeCts?.Cancel());
        cancelExtractBtn.Enabled = false;
        actionRow.AddView(extractBtn, new LinearLayout.LayoutParams(0, Dp(44), 2.2f) { RightMargin = Dp(6) });
        actionRow.AddView(cancelExtractBtn, new LinearLayout.LayoutParams(0, Dp(44), 1f));
        card.AddView(actionRow);

        extractProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0
        };
        OneUiTheme.StyleProgressBar(this, extractProgressBar);
        card.AddView(extractProgressBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(8)) { TopMargin = Dp(4) });

        extractStatusText = Label(S["Ready"], 12f, ColCyan);
        extractStatusText.SetPadding(0, Dp(6), 0, Dp(4));
        card.AddView(extractStatusText);

        page.AddView(card);

        LinearLayout outCard = Card("Arquivos Extraídos / Output");
        extractedFilesContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        outCard.AddView(extractedFilesContainer);
        page.AddView(outCard);

        RefreshExtractorChecklist();
        return page;
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
        int mode = extractModeSpinner.SelectedItemPosition;
        BitDepth bits = extractBitsSpinner.SelectedItemPosition switch {
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
            row.Background = OneUiTheme.CreateRoundedDrawable(this, ColCardAlt, 18f);
            row.SetPadding(Dp(12), Dp(8), Dp(10), Dp(8));
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
