using System.IO;

using Android.Views;
using Android.Widget;

using Cavern.Filters;
using Cavern.ProAudio;

using Cavernize.Logic.Spatial.Adapters;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    readonly QuickEqCalibrationService quickEqService = new();

    /// <summary>
    /// Opens the One UI 9 SecQSDetail modal for multi-point Farina ESS QuickEQ calibration,
    /// FDW+ERB spatial averaging, Cytomic SVF solving, and universal EQ/FIR export.
    /// </summary>
    void ShowQuickEqCalibrationDialog() {
        if (uiDestroyed || store == null) {
            return;
        }

        LinearLayout root = new(this) { Orientation = Orientation.Vertical };

        int initialTarget = Math.Clamp(store.QuickEqTargetIndex, 0, 3);
        quickEqService.TargetCurve = (QuickEqTargetCurve)initialTarget;

        OneUiInlineSelector targetSelector = new(this, S["QuickEqTargetLabel"]);
        targetSelector.SetItems(["Harman", "Brüel & Kjær", "Warm Tilt", "Flat"], initialTarget);
        targetSelector.SelectionChanged += pos => {
            int clamped = Math.Clamp(pos, 0, 3);
            quickEqService.TargetCurve = (QuickEqTargetCurve)clamped;
            store.QuickEqTargetIndex = clamped;
        };
        root.AddView(targetSelector, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView pointsBadgeTv = Label("", 12.2f, ColAccent);
        pointsBadgeTv.Typeface = OneUiTheme.GetAppleFontBold(this);
        pointsBadgeTv.SetPadding(0, Dp(2), 0, Dp(4));
        root.AddView(pointsBadgeTv);

        TextView statusTv = Label("", 11.8f, OneUiTheme.TextSub);
        statusTv.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        statusTv.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            OneUiTheme.SurfaceSubTile,
            12f,
            OneUiTheme.BorderSubtle,
            0.8f);
        root.AddView(statusTv, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });

        void RefreshQuickEqDialogStatus() {
            int count = quickEqService.TakeCount;
            pointsBadgeTv.Text = string.Format(S["QuickEqPointsBadgeFmt"], count, QuickEqCalibrationService.MaxPoints);
            if (count > 0) {
                List<QuickEqTakeSummary> takes = quickEqService.GetTakesSnapshot();
                System.Text.StringBuilder sb = new();
                for (int i = 0; i < takes.Count; i++) {
                    if (i > 0) sb.Append('\n');
                    sb.Append(takes[i].StatusNote);
                }
                statusTv.Text = sb.ToString();
            } else if (!string.IsNullOrWhiteSpace(store.QuickEqLastSummary)) {
                statusTv.Text = store.QuickEqLastSummary;
            } else {
                statusTv.Text = S["QuickEqStatusEmpty"];
            }
        }

        static void ConfigureWrapButton(Button b) {
            b.SetSingleLine(false);
            b.SetMaxLines(2);
            b.Ellipsize = null;
        }

        LinearLayout primaryRow = HRow();
        primaryRow.SetPadding(0, 0, 0, Dp(8));

        Button takeSweepBtn = null;
        takeSweepBtn = PrimaryButton(S["QuickEqTakeSweepBtn"], async () => {
            if (!RequestRecordAudioPermissionIfNeeded()) {
                ShowToast(S["ProAudioMicPermNeededToast"]);
                return;
            }
            if (takeSweepBtn != null) {
                takeSweepBtn.Enabled = false;
            }
            try {
                QuickEqTakeSummary take = await quickEqService.CaptureTakeAsync(playerEngine, audioOutput);
                if (uiDestroyed) {
                    return;
                }
                if (take.IsValid) {
                    ShowToast(string.Format(
                        S["QuickEqTakeDoneFmt"],
                        take.PointIndex,
                        QuickEqCalibrationService.MaxPoints,
                        take.SnrDb,
                        take.Harmonic2Db));
                } else {
                    ShowToast(take.StatusNote);
                }
                RefreshQuickEqDialogStatus();
            } finally {
                if (!uiDestroyed && takeSweepBtn != null) {
                    takeSweepBtn.Enabled = true;
                }
            }
        });
        ConfigureWrapButton(takeSweepBtn);

        Button solveApplyBtn = PillButton(S["QuickEqSolveApplyBtn"], () => {
            if (quickEqService.TakeCount == 0) {
                ShowToast(S["QuickEqStatusEmpty"]);
                return;
            }
            QuickEqTargetCurve target = (QuickEqTargetCurve)Math.Clamp(targetSelector.SelectedIndex, 0, 3);
            QuickEqCalibrationResult res = quickEqService.SolveCurrentSession(target);
            FilterStudioBandSpec[] specs = QuickEqCalibrationService.ApplyAndSave(res, store, playerEngine, enable: true);
            store.FilterStudioNyquistMatched = playerEngine?.FilterStudioStage?.NyquistMatched ?? true;
            store.FilterStudioBandsJson = playerEngine?.FilterStudioStage?.SerializeBandsJson() ?? "";
            store.FilterStudioEnabled = true;
            RefreshFilterStudioSummaryUi();
            RefreshSoundHubCardsUi();
            RefreshQuickEqDialogStatus();
            ShowToast(string.Format(S["QuickEqAppliedFmt"], specs.Length, res.PreampDb));
        });
        ConfigureWrapButton(solveApplyBtn);

        primaryRow.AddView(takeSweepBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        primaryRow.AddView(solveApplyBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(primaryRow);

        LinearLayout secondaryRow = HRow();
        Button clearBtn = PillButton(S["QuickEqClearTakesBtn"], () => {
            quickEqService.ClearSession();
            RefreshQuickEqDialogStatus();
        });
        ConfigureWrapButton(clearBtn);

        Button exportBtn = PillButton(S["QuickEqExportBtn"], ShowQuickEqExportDialog);
        ConfigureWrapButton(exportBtn);

        secondaryRow.AddView(clearBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        secondaryRow.AddView(exportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(secondaryRow);

        RefreshQuickEqDialogStatus();

        OneUiModalDialog.Show(
            this,
            S["QuickEqTitle"],
            S["QuickEqSub"],
            customContent: root,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconStemsCalib,
            positiveText: S["CloseBtn"]);
    }

    void ShowQuickEqExportDialog() {
        if (uiDestroyed || store == null) {
            return;
        }
        List<FilterStudioBandSpec> bands = [];
        if (quickEqService.TakeCount > 0) {
            QuickEqCalibrationResult solved = quickEqService.SolveCurrentSession();
            bands = new List<FilterStudioBandSpec>(QuickEqSweepEngine.ToExportBands(solved.Bands));
        }
        if (bands.Count == 0) {
            bands = QuickEqCalibrationService.LoadSavedBands(store);
        }
        if (bands.Count == 0 && playerEngine?.FilterStudioStage != null) {
            bands = playerEngine.FilterStudioStage.GetBandsSnapshot();
        }
        if (bands.Count == 0) {
            ShowToast(S["QuickEqStatusEmpty"]);
            return;
        }

        int sr = Math.Max(44100, playerEngine?.OutputSampleRate ?? 48000);
        LinearLayout wrap = new(this) { Orientation = Orientation.Vertical };

        void AddQuickEqExportRow(string label, EqExportFormat fmt) {
            Button b = PillButton(label, () => {
                try {
                    string outPath = QuickEqCalibrationService.ExportToFile(
                        this,
                        bands,
                        fmt,
                        sr,
                        GetSelectedExportDestination());
                    ShowToast(string.Format(S["FilterStudioExportSavedFmt"], Path.GetFileName(outPath)));
                } catch (Exception ex) {
                    ShowToast(ex.Message);
                }
            });
            b.SetSingleLine(false);
            b.SetMaxLines(2);
            b.Ellipsize = null;
            b.SetMinimumHeight(Dp(44));
            wrap.AddView(b, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });
        }

        AddQuickEqExportRow("Equalizer APO (.txt)", EqExportFormat.EqualizerApo);
        AddQuickEqExportRow("CamillaDSP YAML (.yml)", EqExportFormat.CamillaDsp);
        AddQuickEqExportRow("MiniDSP Biquads (.txt)", EqExportFormat.MiniDsp);
        AddQuickEqExportRow("PipeWire Filter-Chain (.conf)", EqExportFormat.PipeWire);
        AddQuickEqExportRow("Minimum-Phase FIR Impulse (.wav)", EqExportFormat.FirWav);

        OneUiModalDialog.Show(
            this,
            S["QuickEqExportTitle"],
            customContent: wrap,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconStemsCalib,
            positiveText: S["CloseBtn"]);
    }
}
