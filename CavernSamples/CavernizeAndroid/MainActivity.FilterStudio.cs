using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Renderers;
using Cavern.Format.Transcoders;
using Cavern.Restoration;
using Cavern.Waveforms.Utilities;
using Cavernize.Logic.CommandLine.HiddenCommands;
using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial.Adapters;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

/// <summary>
/// Wave C3 (W57.3): 2026-SOTA FilterStudio (click-free Cytomic SVF + Vicanek/Orfanidis Nyquist-decramped EQ +
/// real-time spectrum/phase graph + universal Equalizer APO / CamillaDSP / MiniDSP / PipeWire / FIR WAV export)
/// and V4 Export -> Tools (ADMValidator BS.2076-2, EnhancedAC3Merger, WAVChannelReorderer, --override-bed w/
/// SubSampleSyncEngine, --extract, and --mlp-presentation).
/// </summary>
public partial class MainActivity {
    LinearLayout exportToolsSection;
    TextView filterStudioSummaryTv;
    TextView exportToolsStatusTv;
    OneUiInlineSelector toolsMlpPresSelector;
    OneUiInlineSelector toolsWavMapSelector;
    int selectedWavReorderPreset;
    string overrideBedEac3Path = "";
    string overrideBedLosslessPath = "";
    TextView overrideBedStatusTv;
    string mergeBaseEac3Path = "";
    string mergeDepEac3Path = "";
    TextView mergeEac3StatusTv;

    /// <summary>
    /// Synchronizes persisted FilterStudio EQ settings and MLP presentation override into live engines.
    /// </summary>
    void SyncFilterStudioAndToolsFromStore() {
        if (store == null) {
            return;
        }
        MeridianLosslessPackingRenderer.ForcedPresentation = store.MlpPresentation >= 0 ? store.MlpPresentation : null;
        if (playerEngine?.FilterStudioStage != null) {
            FilterStudioPostProcessingStage fs = playerEngine.FilterStudioStage;
            fs.NyquistMatched = store.FilterStudioNyquistMatched;
            fs.LoadBandsJson(store.FilterStudioBandsJson);
            fs.UserEnabled = store.FilterStudioEnabled;
        }
        RefreshFilterStudioSummaryUi();
    }

    void RefreshFilterStudioSummaryUi() {
        if (filterStudioSummaryTv == null || store == null) {
            return;
        }
        int bandsCount = playerEngine?.FilterStudioStage?.GetBandsSnapshot().Count ?? 5;
        string state = store.FilterStudioEnabled ? S["FilterStudioStateOn"] : S["FilterStudioStateOff"];
        string nyq = store.FilterStudioNyquistMatched ? S["FilterStudioNyquistTag"] : "SVF";
        filterStudioSummaryTv.Text = string.Format(S["FilterStudioSummaryFmt"], state, bandsCount, nyq);
    }

    /// <summary>
    /// Compact FilterStudio card mounted inside Sound Pro/Lab (Category 0) and launchable from Sound Hub.
    /// </summary>
    LinearLayout BuildFilterStudioProCard() {
        LinearLayout card = Card(S["FilterStudioTitle"], OneUiIconType.Sliders, OneUiTheme.IconStudioExport);
        filterStudioSummaryTv = Label("", 11.8f, OneUiTheme.TextSub);
        filterStudioSummaryTv.SetPadding(0, 0, 0, Dp(8));
        card.AddView(filterStudioSummaryTv);

        LinearLayout row = HRow();
        Button openBtn = PrimaryButton(S["FilterStudioOpenBtn"], ShowFilterStudioDialog);
        Button exportBtn = PillButton(S["FilterStudioExportBtn"], ShowFilterStudioExportDialog);
        row.AddView(openBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.35f) { RightMargin = Dp(8) });
        row.AddView(exportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(row);
        RefreshFilterStudioSummaryUi();
        return card;
    }

    /// <summary>
    /// Interactive FilterStudio One UI 9 SecQSDetail modal with 120 Hz log graph, draggable nodes,
    /// live click-free Cytomic SVF processing, Nyquist decramping, and universal export.
    /// </summary>
    void ShowFilterStudioDialog() {
        if (uiDestroyed || playerEngine?.FilterStudioStage == null) {
            return;
        }
        FilterStudioPostProcessingStage stage = playerEngine.FilterStudioStage;
        List<FilterStudioBandSpec> bands = stage.GetBandsSnapshot();
        if (bands.Count == 0) {
            bands = FilterStudioPostProcessingStage.CreateDefaultBands();
            stage.SetBands(bands);
        }
        int selIdx = 0;

        LinearLayout root = new(this) { Orientation = Orientation.Vertical };

        // Top switches: Master Enable + Nyquist Decramped + Phase Overlay
        AddSwitchRow(root, S["FilterStudioEnableToggle"], store.FilterStudioEnabled, v => {
            store.FilterStudioEnabled = v;
            stage.UserEnabled = v;
            RefreshFilterStudioSummaryUi();
        });
        AddSwitchRow(root, S["FilterStudioNyquistToggle"], store.FilterStudioNyquistMatched, v => {
            store.FilterStudioNyquistMatched = v;
            stage.NyquistMatched = v;
            for (int i = 0; i < bands.Count; i++) {
                bands[i].NyquistMatched = v;
            }
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            RefreshFilterStudioSummaryUi();
        });

        FilterStudioGraphView graph = new(this, stage) {
            ShowPhaseOverlay = true
        };
        graph.SetBands(bands, selIdx);
        root.AddView(graph, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(205)) {
            TopMargin = Dp(6),
            BottomMargin = Dp(8)
        });

        AddSwitchRow(root, S["FilterStudioPhaseToggle"], true, v => graph.ShowPhaseOverlay = v);

        // Band selector chip strip + Add / Remove / Reset
        HorizontalScrollView bandScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout bandStrip = HRow();
        bandScroll.AddView(bandStrip);
        root.AddView(bandScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(6)
        });

        // Selected band parameter readout & controls
        TextView bandInfoTv = Label("", 12f, ColText);
        bandInfoTv.SetTypeface(null, TypefaceStyle.Bold);
        bandInfoTv.SetPadding(0, Dp(2), 0, Dp(6));
        root.AddView(bandInfoTv);

        OneUiInlineSelector typeSelector = new(this, S["FilterStudioTypeLabel"]);
        string[] typeLabels = ["Peak", "LowShelf", "HighShelf", "Lowpass", "Highpass", "Bandpass", "Notch", "Allpass"];
        CytomicSvfType[] typeValues = [
            CytomicSvfType.Peak, CytomicSvfType.LowShelf, CytomicSvfType.HighShelf,
            CytomicSvfType.Lowpass, CytomicSvfType.Highpass, CytomicSvfType.Bandpass,
            CytomicSvfType.Notch, CytomicSvfType.Allpass
        ];
        root.AddView(typeSelector);

        bool suppressSlider = false;
        TextView freqValTv = null, gainValTv = null, qValTv = null;
        (SeekBar freqSeek, TextView freqTv) = AddSliderRow(root, S["FilterStudioFreqLabel"], 0, 1000, 500, pos => {
            if (suppressSlider || selIdx < 0 || selIdx >= bands.Count) {
                return;
            }
            double hz = Math.Round(20.0 * Math.Pow(1000.0, pos / 1000.0));
            bands[selIdx].FrequencyHz = hz;
            stage.UpdateBandLive(selIdx, hz, bands[selIdx].GainDb, bands[selIdx].Q);
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            graph.SetBands(bands, selIdx);
            UpdateBandInfo();
        }, " Hz");
        freqValTv = freqTv;

        (SeekBar gainSeek, TextView gainTv) = AddSliderRow(root, S["FilterStudioGainLabel"], 0, 144, 72, pos => {
            if (suppressSlider || selIdx < 0 || selIdx >= bands.Count) {
                return;
            }
            double db = (pos - 72) * 0.25;
            bands[selIdx].GainDb = db;
            stage.UpdateBandLive(selIdx, bands[selIdx].FrequencyHz, db, bands[selIdx].Q);
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            graph.SetBands(bands, selIdx);
            UpdateBandInfo();
        }, " dB", defaultValue: 72);
        gainValTv = gainTv;

        (SeekBar qSeek, TextView qTv) = AddSliderRow(root, S["FilterStudioQLabel"], 2, 160, 10, pos => {
            if (suppressSlider || selIdx < 0 || selIdx >= bands.Count) {
                return;
            }
            double qv = pos * 0.1;
            bands[selIdx].Q = qv;
            stage.UpdateBandLive(selIdx, bands[selIdx].FrequencyHz, bands[selIdx].GainDb, qv);
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            graph.SetBands(bands, selIdx);
            UpdateBandInfo();
        }, "", defaultValue: 10);
        qValTv = qTv;

        void UpdateBandInfo() {
            if (selIdx < 0 || selIdx >= bands.Count) {
                return;
            }
            FilterStudioBandSpec b = bands[selIdx];
            bandInfoTv.Text = $"#{selIdx + 1} · {b.Type} · {b.FrequencyHz:0} Hz · {b.GainDb:+0.0;-0.0;0.0} dB · Q {b.Q:0.00}";
            if (freqValTv != null) freqValTv.Text = $"{b.FrequencyHz:0} Hz";
            if (gainValTv != null) gainValTv.Text = $"{b.GainDb:+0.0;-0.0;0.0} dB";
            if (qValTv != null) qValTv.Text = $"{b.Q:0.00}";
        }

        void SyncControlsToSelected() {
            if (selIdx < 0 || selIdx >= bands.Count) {
                return;
            }
            suppressSlider = true;
            FilterStudioBandSpec b = bands[selIdx];
            int tIdx = Array.IndexOf(typeValues, b.Type);
            typeSelector.SetItems(typeLabels, tIdx >= 0 ? tIdx : 0);
            freqSeek.Progress = Math.Clamp((int)Math.Round(Math.Log10(Math.Clamp(b.FrequencyHz, 20.0, 20000.0) / 20.0) / 3.0 * 1000.0), 0, 1000);
            gainSeek.Progress = Math.Clamp((int)Math.Round(b.GainDb / 0.25) + 72, 0, 144);
            qSeek.Progress = Math.Clamp((int)Math.Round(b.Q * 10.0), 2, 160);
            suppressSlider = false;
            UpdateBandInfo();
        }

        void RebuildBandStrip() {
            bandStrip.RemoveAllViews();
            for (int i = 0; i < bands.Count; i++) {
                int captured = i;
                Button chip = PillButton($"B{i + 1}", () => {
                    selIdx = captured;
                    graph.SelectedBandIndex = selIdx;
                    RebuildBandStrip();
                    SyncControlsToSelected();
                }, active: i == selIdx);
                chip.TextSize = 11.5f;
                chip.SetMinimumHeight(Dp(34));
                chip.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
                bandStrip.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                    RightMargin = Dp(6)
                });
            }
            if (bands.Count < FilterStudioPostProcessingStage.MaxBands) {
                Button addBtn = PillButton("+ " + S["FilterStudioAddBandBtn"], () => {
                    bands.Add(new FilterStudioBandSpec(CytomicSvfType.Peak, 1000.0, 0.0, 1.0, true, store.FilterStudioNyquistMatched));
                    selIdx = bands.Count - 1;
                    stage.SetBands(bands);
                    store.FilterStudioBandsJson = stage.SerializeBandsJson();
                    graph.SetBands(bands, selIdx);
                    RebuildBandStrip();
                    SyncControlsToSelected();
                    RefreshFilterStudioSummaryUi();
                });
                addBtn.TextSize = 11.5f;
                addBtn.SetMinimumHeight(Dp(34));
                bandStrip.AddView(addBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                    RightMargin = Dp(6)
                });
            }
            if (bands.Count > 1) {
                Button delBtn = PillButton(S["FilterStudioRemoveBandBtn"], () => {
                    bands.RemoveAt(selIdx);
                    selIdx = Math.Clamp(selIdx, 0, bands.Count - 1);
                    stage.SetBands(bands);
                    store.FilterStudioBandsJson = stage.SerializeBandsJson();
                    graph.SetBands(bands, selIdx);
                    RebuildBandStrip();
                    SyncControlsToSelected();
                    RefreshFilterStudioSummaryUi();
                });
                delBtn.TextSize = 11.5f;
                delBtn.SetMinimumHeight(Dp(34));
                bandStrip.AddView(delBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            }
        }

        typeSelector.SelectionChanged += pos => {
            if (suppressSlider || selIdx < 0 || selIdx >= bands.Count || pos < 0 || pos >= typeValues.Length) {
                return;
            }
            bands[selIdx].Type = typeValues[pos];
            stage.UpdateBandLive(selIdx, bands[selIdx].FrequencyHz, bands[selIdx].GainDb, bands[selIdx].Q, typeValues[pos]);
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            graph.SetBands(bands, selIdx);
            UpdateBandInfo();
        };

        graph.BandSelected += idx => {
            selIdx = idx;
            RebuildBandStrip();
            SyncControlsToSelected();
        };
        graph.BandDragged += (idx, hz, db) => {
            selIdx = idx;
            stage.UpdateBandLive(idx, hz, db, bands[idx].Q);
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            SyncControlsToSelected();
        };

        LinearLayout actionRow = HRow();
        actionRow.SetPadding(0, Dp(8), 0, 0);
        Button flatBtn = PillButton(S["FilterStudioResetFlatBtn"], () => {
            bands = FilterStudioPostProcessingStage.CreateDefaultBands();
            selIdx = 0;
            stage.SetBands(bands);
            store.FilterStudioBandsJson = stage.SerializeBandsJson();
            graph.SetBands(bands, selIdx);
            RebuildBandStrip();
            SyncControlsToSelected();
            RefreshFilterStudioSummaryUi();
        });
        Button exportBtn = PrimaryButton(S["FilterStudioExportBtn"], ShowFilterStudioExportDialog);
        actionRow.AddView(flatBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        actionRow.AddView(exportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.3f));
        root.AddView(actionRow);

        RebuildBandStrip();
        SyncControlsToSelected();

        OneUiModalDialog.Show(
            this,
            S["FilterStudioTitle"],
            S["FilterStudioSubtitle"],
            customContent: root,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconStudioExport,
            positiveText: S["CloseBtn"]);
    }

    /// <summary>
    /// Universal EQ / FIR exporter modal supporting Equalizer APO, CamillaDSP, MiniDSP, PipeWire, and FIR WAV.
    /// </summary>
    void ShowFilterStudioExportDialog() {
        if (uiDestroyed || playerEngine?.FilterStudioStage == null) {
            return;
        }
        FilterStudioPostProcessingStage stage = playerEngine.FilterStudioStage;
        List<FilterStudioBandSpec> bands = stage.GetBandsSnapshot();
        int sr = Math.Max(44100, stage.SampleRate);
        double preampDb = EqExportFormatter.ComputeAutoPreampDb(bands, sr);

        LinearLayout wrap = new(this) { Orientation = Orientation.Vertical };
        void AddExportRow(string label, EqExportFormat fmt, string ext) {
            Button b = PillButton(label, () => {
                try {
                    string outDir = ResolveToolsExportDir("FilterStudio");
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    string outPath = Path.Combine(outDir, $"Cavern_FilterStudio_{fmt}_{stamp}.{ext}");
                    if (fmt == EqExportFormat.FirWav) {
                        float[] ir = EqExportFormatter.RenderFirImpulse(bands, sr, 4096, preampDb);
                        using RIFFWaveWriter writer = new(outPath, 1, ir.Length, sr, BitDepth.Float32);
                        writer.WriteHeader();
                        writer.WriteBlock(ir, 0, ir.Length);
                    } else {
                        string content = fmt switch {
                            EqExportFormat.EqualizerApo => EqExportFormatter.ToEqualizerApo(bands, preampDb),
                            EqExportFormat.CamillaDsp => EqExportFormatter.ToCamillaDspYaml(bands, sr, preampDb),
                            EqExportFormat.MiniDsp => EqExportFormatter.ToMiniDspBiquads(bands, sr),
                            _ => EqExportFormatter.ToPipeWireFilterChain(bands, preampDb)
                        };
                        File.WriteAllText(outPath, content, Encoding.UTF8);
                    }
                    ShowToast(string.Format(S["FilterStudioExportSavedFmt"], Path.GetFileName(outPath)));
                } catch (Exception ex) {
                    ShowToast(ex.Message);
                }
            });
            b.SetMinimumHeight(Dp(44));
            wrap.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });
        }

        AddExportRow("Equalizer APO (.txt)", EqExportFormat.EqualizerApo, "txt");
        AddExportRow("CamillaDSP YAML (.yml)", EqExportFormat.CamillaDsp, "yml");
        AddExportRow("MiniDSP Biquads (.txt)", EqExportFormat.MiniDsp, "txt");
        AddExportRow("PipeWire Filter-Chain (.conf)", EqExportFormat.PipeWire, "conf");
        AddExportRow("Minimum-Phase FIR Impulse (.wav)", EqExportFormat.FirWav, "wav");

        OneUiModalDialog.Show(
            this,
            S["FilterStudioExportTitle"],
            string.Format(S["FilterStudioExportSubFmt"], bands.Count, sr, preampDb),
            customContent: wrap,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconStudioExport,
            positiveText: S["CloseBtn"]);
    }

    // =========================================================================
    // V4 Export -> Tools (W57.3 Dormant Cavern Core & Suite Utilities)
    // =========================================================================

    LinearLayout BuildExportToolsSection() {
        exportToolsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };

        // Status banner
        exportToolsStatusTv = Label(S["ExportToolsReadyHint"], 11.8f, OneUiTheme.TextSub);
        exportToolsStatusTv.SetPadding(Dp(4), 0, Dp(4), Dp(8));
        exportToolsSection.AddView(exportToolsStatusTv);

        // 1. ADM Validator (ITU-R BS.2076-2)
        LinearLayout admCard = Card(S["ToolAdmValidatorTitle"], OneUiIconType.Info, OneUiTheme.IconStudioExport);
        admCard.AddView(Label(S["ToolAdmValidatorDesc"], 11.8f, OneUiTheme.TextSub));
        Button admBtn = PrimaryButton(S["ToolAdmValidatorBtn"], () => PickToolFile(S["ToolAdmValidatorTitle"], [".wav", ".xml", ".axml"], RunAdmValidationOnFile));
        admCard.AddView(admBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });
        exportToolsSection.AddView(admCard);

        // 2. Enhanced AC-3 Substream Merger (5.1 + dependent -> 7.1 / 9.1.6)
        LinearLayout eac3Card = Card(S["ToolEac3MergerTitle"], OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix);
        eac3Card.AddView(Label(S["ToolEac3MergerDesc"], 11.8f, OneUiTheme.TextSub));
        mergeEac3StatusTv = Label(S["ToolPickTwoFilesHint"], 11.2f, OneUiTheme.TextMuted);
        mergeEac3StatusTv.SetPadding(0, Dp(6), 0, Dp(6));
        eac3Card.AddView(mergeEac3StatusTv);
        LinearLayout mergePickRow = HRow();
        Button pickBaseBtn = PillButton(S["ToolPickBaseEac3Btn"], () => PickToolFile(S["ToolPickBaseEac3Btn"], [".ec3", ".eac3", ".ac3"], p => {
            mergeBaseEac3Path = p;
            RefreshMergeEac3Status();
        }));
        Button pickDepBtn = PillButton(S["ToolPickDepEac3Btn"], () => PickToolFile(S["ToolPickDepEac3Btn"], [".ec3", ".eac3", ".ac3"], p => {
            mergeDepEac3Path = p;
            RefreshMergeEac3Status();
        }));
        mergePickRow.AddView(pickBaseBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        mergePickRow.AddView(pickDepBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        eac3Card.AddView(mergePickRow);
        Button mergeRunBtn = PrimaryButton(S["ToolRunEac3MergeBtn"], RunEnhancedAc3MergeTool);
        eac3Card.AddView(mergeRunBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });
        exportToolsSection.AddView(eac3Card);

        // 3. WAV Channel Reorderer (InterlacedWaveformReorderer)
        LinearLayout wavCard = Card(S["ToolWavReorderTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        wavCard.AddView(Label(S["ToolWavReorderDesc"], 11.8f, OneUiTheme.TextSub));
        toolsWavMapSelector = new OneUiInlineSelector(this, S["ToolWavMapLabel"]);
        string[] wavPresets = [
            "SMPTE 5.1 -> Film (L,C,R,Ls,Rs,LFE)",
            "Film 5.1 -> SMPTE (L,R,C,LFE,Ls,Rs)",
            "Swap Surrounds (4 <-> 5)",
            "Swap Stereo L <-> R"
        ];
        toolsWavMapSelector.SetItems(wavPresets, selectedWavReorderPreset);
        toolsWavMapSelector.SelectionChanged += pos => selectedWavReorderPreset = pos;
        wavCard.AddView(toolsWavMapSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6),
            BottomMargin = Dp(8)
        });
        Button wavRunBtn = PrimaryButton(S["ToolRunWavReorderBtn"], () => PickToolFile(S["ToolWavReorderTitle"], [".wav"], RunWavChannelReorderTool));
        wavCard.AddView(wavRunBtn);
        exportToolsSection.AddView(wavCard);

        // 4. Lossless Bed Override (--override-bed phase-aligned via SubSampleSyncEngine)
        LinearLayout bedCard = Card(S["ToolOverrideBedTitle"], OneUiIconType.Sparkles, OneUiTheme.IconStemsCalib);
        bedCard.AddView(Label(S["ToolOverrideBedDesc"], 11.8f, OneUiTheme.TextSub));
        overrideBedStatusTv = Label(S["ToolPickTwoFilesHint"], 11.2f, OneUiTheme.TextMuted);
        overrideBedStatusTv.SetPadding(0, Dp(6), 0, Dp(6));
        bedCard.AddView(overrideBedStatusTv);
        LinearLayout bedPickRow = HRow();
        Button pickBedEac3Btn = PillButton(S["ToolPickEac3JocBtn"], () => PickToolFile(S["ToolPickEac3JocBtn"], [".ec3", ".eac3", ".ac3"], p => {
            overrideBedEac3Path = p;
            RefreshOverrideBedStatus();
        }));
        Button pickBedWavBtn = PillButton(S["ToolPickLosslessBedBtn"], () => PickToolFile(S["ToolPickLosslessBedBtn"], [".wav", ".flac", ".wv"], p => {
            overrideBedLosslessPath = p;
            RefreshOverrideBedStatus();
        }));
        bedPickRow.AddView(pickBedEac3Btn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        bedPickRow.AddView(pickBedWavBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bedCard.AddView(bedPickRow);
        Button runBedBtn = PrimaryButton(S["ToolRunOverrideBedBtn"], RunOverrideBedSyncTool);
        bedCard.AddView(runBedBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });
        exportToolsSection.AddView(bedCard);

        // 5. Raw Elementary Track Extractor (--extract) & TrueHD MLP Presentation (--mlp-presentation)
        LinearLayout rawCard = Card(S["ToolRawMlpTitle"], OneUiIconType.Queue, OneUiTheme.IconSystemInfo);
        rawCard.AddView(Label(S["ToolRawMlpDesc"], 11.8f, OneUiTheme.TextSub));

        toolsMlpPresSelector = new OneUiInlineSelector(this, S["ToolMlpPresLabel"]);
        string[] mlpLabels = [
            S["ToolMlpAuto"],
            "Presentation 0 (2.0 Stereo)",
            "Presentation 1 (5.1 / 7.1 Bed)",
            "Presentation 2 (Atmos 16ch)",
            "Presentation 3 (Extended)"
        ];
        int curMlpIdx = Math.Clamp(store.MlpPresentation + 1, 0, mlpLabels.Length - 1);
        toolsMlpPresSelector.SetItems(mlpLabels, curMlpIdx);
        toolsMlpPresSelector.SelectionChanged += pos => {
            int val = pos - 1;
            store.MlpPresentation = val;
            MeridianLosslessPackingRenderer.ForcedPresentation = val >= 0 ? val : null;
            SetToolsStatus($"--mlp-presentation = {(val < 0 ? "Auto" : val.ToString())}");
        };
        rawCard.AddView(toolsMlpPresSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6),
            BottomMargin = Dp(8)
        });

        Button extractRawBtn = PillButton(S["ToolExtractRawTrackBtn"], RunRawTrackExtractTool);
        rawCard.AddView(extractRawBtn);
        exportToolsSection.AddView(rawCard);

        return exportToolsSection;
    }

    string ResolveToolsExportDir(string subFolder) {
        string baseDir = AndroidFileHelper.GetExportWorkingDir(this, GetSelectedExportDestination(), ".wav", out _, out _);
        string dir = Path.Combine(baseDir, subFolder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    void PickToolFile(string title, string[] extensions, Action<string> onSelected) =>
        OpenFileWithExplorerForExtensions(title, extensions, onSelected, ReqLoadFilters, allowAllFiles: true);

    void SetToolsStatus(string msg) {
        RunOnUiThread(() => {
            if (exportToolsStatusTv != null) {
                exportToolsStatusTv.Text = msg;
            }
        });
    }

    void RefreshMergeEac3Status() {
        if (mergeEac3StatusTv == null) {
            return;
        }
        string a = string.IsNullOrEmpty(mergeBaseEac3Path) ? "—" : Path.GetFileName(mergeBaseEac3Path);
        string b = string.IsNullOrEmpty(mergeDepEac3Path) ? "—" : Path.GetFileName(mergeDepEac3Path);
        mergeEac3StatusTv.Text = $"Base: {a}  |  Substream: {b}";
    }

    void RefreshOverrideBedStatus() {
        if (overrideBedStatusTv == null) {
            return;
        }
        string a = string.IsNullOrEmpty(overrideBedEac3Path) ? "—" : Path.GetFileName(overrideBedEac3Path);
        string b = string.IsNullOrEmpty(overrideBedLosslessPath) ? "—" : Path.GetFileName(overrideBedLosslessPath);
        overrideBedStatusTv.Text = $"E-AC-3 JOC: {a}  |  Bed: {b}";
    }

    void RunAdmValidationOnFile(string path) {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
            return;
        }
        SetToolsStatus($"ADM BS.2076-2: {Path.GetFileName(path)}...");
        Task.Run(() => {
            try {
                AudioDefinitionModel adm;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".xml" || ext == ".axml") {
                    using FileStream fs = File.OpenRead(path);
                    adm = new AudioDefinitionModel(fs, fs.Length, false);
                } else {
                    using RIFFWaveReader wav = new(path);
                    wav.ReadHeader();
                    adm = wav.ADM ?? throw new InvalidOperationException("WAV file contains no ADM axml chunk.");
                }
                AdmValidationReport report = adm.ValidateBs2076_2();
                StringBuilder sb = new();
                sb.AppendLine(report.Summary);
                sb.AppendLine();
                if (report.Errors.Count > 0) {
                    sb.AppendLine("ERRORS:");
                    for (int i = 0; i < Math.Min(report.Errors.Count, 24); i++) {
                        sb.Append("• ").AppendLine(report.Errors[i]);
                    }
                }
                if (report.Warnings.Count > 0) {
                    sb.AppendLine("WARNINGS:");
                    for (int i = 0; i < Math.Min(report.Warnings.Count, 16); i++) {
                        sb.Append("• ").AppendLine(report.Warnings[i]);
                    }
                }
                if (report.IsBs2076Compliant && report.Warnings.Count == 0) {
                    sb.AppendLine("All ITU-R BS.2076-2 checks passed with 0 errors and 0 warnings.");
                }
                string body = sb.ToString();
                SetToolsStatus(report.Summary);
                RunOnUiThread(() => OneUiModalDialog.Show(
                    this,
                    S["ToolAdmValidatorTitle"],
                    body,
                    icon: OneUiIconType.Info,
                    iconBadgeColor: report.IsBs2076Compliant ? OneUiTheme.FunctionalGreen : OneUiTheme.FunctionalRed,
                    positiveText: S["CloseBtn"],
                    monospaceBody: true));
            } catch (Exception ex) {
                SetToolsStatus($"ADM error: {ex.Message}");
                RunOnUiThread(() => ShowToast(ex.Message));
            }
        });
    }

    void RunEnhancedAc3MergeTool() {
        if (string.IsNullOrEmpty(mergeBaseEac3Path) || string.IsNullOrEmpty(mergeDepEac3Path) ||
            !File.Exists(mergeBaseEac3Path) || !File.Exists(mergeDepEac3Path)) {
            ShowToast(S["ToolPickTwoFilesHint"]);
            return;
        }
        string outDir = ResolveToolsExportDir("Tools");
        string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(mergeBaseEac3Path) + "_merged.ec3");
        RunBackgroundOperation(ct => {
            int chBase, chDep;
            using (EnhancedAC3Reader ra = new(mergeBaseEac3Path)) {
                ra.ReadHeader();
                chBase = ra.ChannelCount;
            }
            using (EnhancedAC3Reader rb = new(mergeDepEac3Path)) {
                rb.ReadHeader();
                chDep = rb.ChannelCount;
            }
            ReferenceChannel[] layout = BuildEac3MergeLayout(chBase + chDep);
            using FileStream fsBase = File.OpenRead(mergeBaseEac3Path);
            using FileStream fsDep = File.OpenRead(mergeDepEac3Path);
            EnhancedAC3Merger merger = new([fsBase, fsDep], layout, outPath);
            while (!ct.IsCancellationRequested && !merger.ProcessFrame()) { }
            SetToolsStatus($"Merged E-AC-3 ({layout.Length}ch) -> {Path.GetFileName(outPath)}");
        }, outPath);
    }

    static ReferenceChannel[] BuildEac3MergeLayout(int totalChannels) {
        ReferenceChannel[] full916 = [
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight,
            ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight,
            ReferenceChannel.WideLeft, ReferenceChannel.WideRight
        ];
        int count = Math.Clamp(totalChannels, 2, full916.Length);
        ReferenceChannel[] layout = new ReferenceChannel[count];
        Array.Copy(full916, layout, count);
        return layout;
    }

    void RunWavChannelReorderTool(string path) {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
            return;
        }
        string outDir = ResolveToolsExportDir("Tools");
        string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "_reordered.wav");
        int preset = selectedWavReorderPreset;
        RunBackgroundOperation(ct => {
            using RIFFWaveReader reader = new(path);
            reader.ReadHeader();
            int ch = reader.ChannelCount;
            int[] map = BuildWavReorderMap(ch, preset);
            InterlacedWaveformReorderer reorderer = new(map);
            using RIFFWaveWriter writer = new(outPath, ch, reader.Length, reader.SampleRate, reader.Bits);
            writer.WriteHeader();
            const int chunkFrames = 4096;
            float[] buf = new float[chunkFrames * ch];
            long remaining = reader.Length;
            while (remaining > 0 && !ct.IsCancellationRequested) {
                int frames = (int)Math.Min(chunkFrames, remaining);
                int count = frames * ch;
                reader.ReadBlock(buf, 0, count);
                if (count == buf.Length) {
                    reorderer.Process(buf);
                } else {
                    float[] tail = new float[count];
                    Array.Copy(buf, tail, count);
                    reorderer.Process(tail);
                    Array.Copy(tail, buf, count);
                }
                writer.WriteBlock(buf, 0, count);
                remaining -= frames;
            }
            SetToolsStatus($"Reordered {ch}ch WAV -> {Path.GetFileName(outPath)}");
        }, outPath);
    }

    static int[] BuildWavReorderMap(int channels, int preset) {
        int[] map = new int[channels];
        for (int i = 0; i < channels; i++) {
            map[i] = i;
        }
        if (preset == 0 && channels >= 6) {
            // SMPTE (L,R,C,LFE,Ls,Rs) -> Film (L,C,R,Ls,Rs,LFE)
            map[0] = 0; map[1] = 2; map[2] = 1; map[3] = 4; map[4] = 5; map[5] = 3;
        } else if (preset == 1 && channels >= 6) {
            // Film (L,C,R,Ls,Rs,LFE) -> SMPTE (L,R,C,LFE,Ls,Rs)
            map[0] = 0; map[1] = 2; map[2] = 1; map[3] = 5; map[4] = 3; map[5] = 4;
        } else if (preset == 2 && channels >= 6) {
            map[4] = 5; map[5] = 4;
        } else if (preset == 3 && channels >= 2) {
            map[0] = 1; map[1] = 0;
        }
        return map;
    }

    void RunOverrideBedSyncTool() {
        if (string.IsNullOrEmpty(overrideBedEac3Path) || string.IsNullOrEmpty(overrideBedLosslessPath) ||
            !File.Exists(overrideBedEac3Path) || !File.Exists(overrideBedLosslessPath)) {
            ShowToast(S["ToolPickTwoFilesHint"]);
            return;
        }
        string outDir = ResolveToolsExportDir("Tools");
        string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(overrideBedEac3Path) + "_overridebed.wav");
        RunBackgroundOperation(ct => {
            // Stage 1: Compute sub-sample phase alignment via SubSampleSyncEngine
            float[] eac3Probe = ReadMonoProbe(overrideBedEac3Path, out int sr);
            float[] bedProbe = ReadMonoProbe(overrideBedLosslessPath, out _);
            SubSampleSyncResult sync = OverrideBedDecoder.AlignOverrideBed(eac3Probe, bedProbe, sr);
            double offset = sync.IsSynced ? sync.BaseOffsetSamples : 0.0;
            SetToolsStatus(sync.IsSynced
                ? $"SubSampleSync: Δ={offset:+0.00;-0.00;0.00} smp (conf={sync.Confidence:0.00}) — rendering..."
                : "OverrideBed: 0-sample direct alignment — rendering...");

            // Stage 2: Decode E-AC-3 JOC with phase-aligned lossless bed override
            using AudioReader bedReader = AudioReader.Open(overrideBedLosslessPath);
            bedReader.ReadHeader();
            using OverrideBedReader ovReader = new(overrideBedEac3Path, bedReader, offset);
            ovReader.ReadHeader();
            int outCh = ovReader.ChannelCount;
            using RIFFWaveWriter writer = new(outPath, outCh, ovReader.Length, ovReader.SampleRate, BitDepth.Int24);
            writer.WriteHeader();
            const int chunkFrames = 2048;
            float[] block = new float[chunkFrames * outCh];
            long remaining = ovReader.Length;
            while (remaining > 0 && !ct.IsCancellationRequested) {
                int frames = (int)Math.Min(chunkFrames, remaining);
                int count = frames * outCh;
                ovReader.ReadBlock(block, 0, count);
                writer.WriteBlock(block, 0, count);
                remaining -= frames;
            }
            SetToolsStatus($"--override-bed OK (Δ={offset:+0.00;-0.00;0.00} smp) -> {Path.GetFileName(outPath)}");
        }, outPath);
    }

    static float[] ReadMonoProbe(string path, out int sampleRate) {
        using AudioReader r = AudioReader.Open(path);
        r.ReadHeader();
        sampleRate = Math.Max(8000, r.SampleRate);
        int ch = Math.Max(1, r.ChannelCount);
        int frames = (int)Math.Min(r.Length, Math.Max(SubSampleSyncEngine.GccFftSize * 2, sampleRate * 4));
        float[] interleaved = new float[frames * ch];
        r.ReadBlock(interleaved, 0, interleaved.Length);
        float[] mono = new float[frames];
        float invCh = 1f / ch;
        for (int f = 0; f < frames; f++) {
            float sum = 0f;
            int b = f * ch;
            for (int c = 0; c < ch; c++) {
                sum += interleaved[b + c];
            }
            mono[f] = sum * invCh;
        }
        return mono;
    }

    void RunRawTrackExtractTool() {
        CavernizeTrack active = GetActiveTrack();
        Track tr = active?.Track;
        if (tr?.Source == null) {
            ShowToast(S["SelectFilePill"]);
            return;
        }
        string outDir = ResolveToolsExportDir("Tools");
        string ext = tr.Format.ToString().ToLowerInvariant();
        string outPath = Path.Combine(outDir, $"track_{ext}.bin");
        RunBackgroundOperation(ct => {
            tr.Source.Seek(0);
            using FileStream fs = File.Create(outPath);
            while (!ct.IsCancellationRequested && tr.IsNextBlockAvailable()) {
                byte[] block = tr.ReadNextBlock();
                if (block != null && block.Length > 0) {
                    fs.Write(block, 0, block.Length);
                }
            }
            SetToolsStatus($"--extract ({tr.Format}) -> {Path.GetFileName(outPath)}");
        }, outPath);
    }
}
