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
    Button selectTargetBtn, wiringBtn, renderBtn, cancelRenderBtn, shareExportBtn, playExportBtn, studioW12ExportBtn;
    LayoutDiagramView layoutDiagram;
    TextView convertStatusText, studioW12ContextText;
    OneUiInlineSelector exportFormatSelector, containerSelector, studioW12PresetSelector;
    LinearLayout studioW12KaraokeContainer;
    OneUiSeekBar studioW12KaraokeBar;
    CheckBox applyRoomToExportCheck;
    ProgressBar convertProgressBar;

    List<string> availableContainerExts = [".wav"];
    int blockSize;
    const string waveExtension = ".wav";
    const int secondFrame = 2 * 1536;
    const double progressSplit = .95;

    LinearLayout BuildConverterTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(6), Dp(14), Dp(116));

        // 0. Active File Source Bar (Clean workspace context & track selector)
        page.AddView(CreateWorkspaceSourceBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 1. System & RenderTarget Hero Card
        LinearLayout sysCard = Card(S["SystemTitle"], OneUiIconType.Speaker, OneUiTheme.IconSpatial);
        LinearLayout sysRow = HRow();

        LinearLayout sysLeft = new(this) { Orientation = Orientation.Vertical };
        LinearLayout rtLabelRow = HRow();
        rtLabelRow.AddView(Label(S["RenderTarget"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        rtLabelRow.AddView(CreateInlineHelpBtn(S["RenderTarget"].TrimEnd(':'), S["TipRenderTarget"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        sysLeft.AddView(rtLabelRow);

        selectTargetBtn = SecondaryButton(RenderTarget?.Name ?? S["SelectTarget"], ShowRenderTargetDialog);
        sysLeft.AddView(selectTargetBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4), BottomMargin = Dp(6) });
        wiringBtn = PillButton(S["DisplayWiring"], DisplayWiringDialog);
        sysLeft.AddView(wiringBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        sysRow.AddView(sysLeft, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(10) });

        layoutDiagram = new LayoutDiagramView(this);
        sysRow.AddView(layoutDiagram);
        sysCard.AddView(sysRow);
        page.AddView(sysCard);

        // 2. Content & Export Card (In-App One UI 9 Selectors — no native Spinner popups)
        LinearLayout expCard = Card("Export", OneUiIconType.Bolt, OneUiTheme.IconStudioExport);

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
            }
        };
        expCard.AddView(exportFormatSelector);

        AddDivider(expCard);
        containerSelector = new OneUiInlineSelector(this, S["Container"]);
        expCard.AddView(containerSelector);

        AddDivider(expCard);
        applyRoomToExportCheck = new CheckBox(this) {
            Text = S["ApplyRoomToExport"],
            Checked = false,
            TextSize = 12.5f
        };
        applyRoomToExportCheck.SetTextColor(ColText);
        applyRoomToExportCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
        expCard.AddView(WrapControlWithHelp(applyRoomToExportCheck, S["ApplyRoomToExport"], S["TipApplyRoomExport"], bottomMarginDp: 2));

        // Action Buttons (non-clipping single-line horizontal rows with BaselineAligned = false)
        LinearLayout actRow1 = HRow();
        actRow1.SetPadding(0, Dp(10), 0, Dp(6));
        renderBtn = PrimaryButton(S["RenderBtn"], () => StartSingleRender());
        cancelRenderBtn = PillButton(S["CancelBtn"], () => activeCts?.Cancel());
        cancelRenderBtn.Enabled = false;
        actRow1.AddView(renderBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2f) { RightMargin = Dp(6) });
        actRow1.AddView(cancelRenderBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        expCard.AddView(actRow1);

        Button queueAddBtn = PillButton(S["AddQueueBtn"], AddCurrentToQueue);
        expCard.AddView(queueAddBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        LinearLayout infoBtns = HRow();
        Button metaBtn = PillButton(S["ShowMetaBtn"], ShowMetadataDialog);
        Button repBtn = PillButton(S["ShowReportBtn"], ShowPostRenderReportDialog);
        infoBtns.AddView(metaBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        infoBtns.AddView(repBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        expCard.AddView(infoBtns);

        // Progress & Result
        convertProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0
        };
        OneUiTheme.StyleProgressBar(this, convertProgressBar);
        LinearLayout.LayoutParams pbLp = new(ViewGroup.LayoutParams.MatchParent, Dp(14)) { TopMargin = Dp(10) };
        expCard.AddView(convertProgressBar, pbLp);

        convertStatusText = Label(S["Ready"], 12f, OneUiTheme.IconSystemInfo);
        convertStatusText.SetPadding(0, Dp(6), 0, Dp(6));
        expCard.AddView(convertStatusText);

        LinearLayout postBtns = HRow();
        shareExportBtn = PillButton(S["ShareFile"], () => ShareLocalFile(lastRenderedFilePath));
        playExportBtn = PillButton(S["OpenPlayer"], () => {
            if (!string.IsNullOrEmpty(lastRenderedFilePath) && File.Exists(lastRenderedFilePath)) {
                OpenLocalPathWithFallbackAsync(lastRenderedFilePath, switchToTab: 0);
            }
        }, active: true);
        shareExportBtn.Visibility = ViewStates.Gone;
        playExportBtn.Visibility = ViewStates.Gone;
        postBtns.AddView(shareExportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        postBtns.AddView(playExportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        expCard.AddView(postBtns);

        page.AddView(expCard);

        // 2B. W12/W14 Studio Master Presets Card (IAMF / HOA 3rd-Order / ADM BWF / Personalized Binaural / Karaoke / Hybrid Master)
        LinearLayout w12Card = Card(S["StudioW12CardTitle"], OneUiIconType.Sparkles, OneUiTheme.IconStudioExport);
        studioW12PresetSelector = new OneUiInlineSelector(this, S["StudioW12PresetLabel"]);
        studioW12PresetSelector.SetItems(new[] {
            S["StudioW12PresetBinaural"],
            S["StudioW12PresetIamf"],
            S["StudioW12PresetHoa"],
            S["StudioW12PresetAdm"],
            S["StudioW12PresetHybrid714"],
            S["StudioW12PresetKaraoke"],
            S["StudioW12PresetLiveMixdown"],
            S["StudioW12PresetFlacAtmos"]
        }, 1);
        studioW12PresetSelector.SelectionChanged += _ => RefreshStudioW12ContextUi();
        w12Card.AddView(studioW12PresetSelector);

        studioW12ContextText = Label("", 11.4f, OneUiTheme.TextSub);
        studioW12ContextText.SetPadding(0, Dp(6), 0, Dp(4));
        w12Card.AddView(studioW12ContextText);

        studioW12KaraokeContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        (studioW12KaraokeBar, _) = AddSliderRow(
            studioW12KaraokeContainer,
            S["StudioW12KaraokeLevelTitle"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100),
            v => {
                store.KaraokeVocalLevel = v * 0.01f;
                playerEngine?.SetKaraokeVocalLevel(store.KaraokeVocalLevel);
                if (karaokeVocalLevelBar != null) {
                    karaokeVocalLevelBar.Progress = v;
                    karaokeVocalLevelBar.Invalidate();
                }
                RefreshLyricsSingDockUi();
                RefreshStudioW12ContextUi();
            },
            "%",
            defaultValue: 5);
        w12Card.AddView(studioW12KaraokeContainer);

        LinearLayout w12BtnRow = HRow();
        w12BtnRow.SetPadding(0, Dp(8), 0, 0);
        studioW12ExportBtn = PrimaryButton(S["StudioW12ExportBtn"], StartStudioW12PresetExport);
        w12BtnRow.AddView(studioW12ExportBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        w12Card.AddView(w12BtnRow);
        page.AddView(w12Card);

        // 3. Batch Render Queue Card
        LinearLayout queueCard = Card(S["QueueTitle"], OneUiIconType.Queue, OneUiTheme.IconAtmosUpmix);
        queueListContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        queueCard.AddView(queueListContainer);

        LinearLayout qBtns = HRow();
        qBtns.SetPadding(0, Dp(10), 0, 0);
        Button batchBtn = PillButton(S["BatchAddBtn"], () => OpenMultiFilesWithExplorer(AddLocalPathsToQueue));
        Button remBtn = PillButton(S["RemoveQueueBtn"], RemoveLastFromQueue);
        Button runQBtn = PrimaryButton(S["ProcessQueueBtn"], StartQueueProcessing);
        qBtns.AddView(batchBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(6) });
        qBtns.AddView(remBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.85f) { RightMargin = Dp(6) });
        qBtns.AddView(runQBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f));
        queueCard.AddView(qBtns);
        page.AddView(queueCard);

        RefreshQueueList();
        RefreshConverterTabState();
        return page;
    }

    void RefreshConverterTabState() {
        if (selectTargetBtn != null && RenderTarget != null) {
            selectTargetBtn.Text = RenderTarget.Name;
        }
        layoutDiagram?.SetRenderTarget(RenderTarget);
        if (wiringBtn != null) {
            wiringBtn.Enabled = RenderTarget is not DriverRenderTarget && RenderTarget is not VirtualizerRenderTarget;
        }
        RefreshContainerOptions();
        RefreshStudioW12ContextUi();
    }

    void RefreshStudioW12ContextUi() {
        if (studioW12ContextText == null || studioW12PresetSelector == null || store == null) {
            return;
        }
        StudioExportPreset preset = (StudioExportPreset)Math.Clamp(studioW12PresetSelector.SelectedIndex, 0, 7);
        bool isKaraokePreset = preset == StudioExportPreset.SpatialKaraokeInstrumental ||
                               preset == StudioExportPreset.LiveKaraokePerformanceMixdown;
        if (studioW12KaraokeContainer != null) {
            studioW12KaraokeContainer.Visibility = isKaraokePreset ? ViewStates.Visible : ViewStates.Gone;
        }
        int vocPct = Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100);
        if (studioW12KaraokeBar != null && studioW12KaraokeBar.Progress != vocPct) {
            studioW12KaraokeBar.Progress = vocPct;
            studioW12KaraokeBar.Invalidate();
        }
        if (preset == StudioExportPreset.FlacAtmosHybridSpatialMaster) {
            Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
            if (lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced) {
                studioW12ContextText.Text = string.Format(S["StudioW12ContextHybridReady"],
                    string.IsNullOrEmpty(lgsr.TwinFormatSummary) ? lgsr.TwinFileName : lgsr.TwinFormatSummary);
                studioW12ContextText.SetTextColor(OneUiTheme.IconSystemInfo);
            } else {
                studioW12ContextText.Text = S["StudioW12ContextHybridNoTwin"];
                studioW12ContextText.SetTextColor(OneUiTheme.PeachText);
            }
        } else if (isKaraokePreset) {
            studioW12ContextText.Text = string.Format(
                S["StudioW12ContextKaraoke"],
                vocPct,
                store.KaraokePreserveBackingVocals ? "ON" : "OFF");
            studioW12ContextText.SetTextColor(OneUiTheme.PrimaryHigh);
        } else {
            studioW12ContextText.Text = string.Format(S["StudioW12ContextGeneral"], store.ResolveTruePeakCeilingDbtp());
            studioW12ContextText.SetTextColor(OneUiTheme.TextSub);
        }
    }

    void RefreshConverterTrackInfo() {
        // Track details & stream switcher are now presented cleanly via WorkspaceSourceBar and ShowTechnicalStreamDialog
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
            sb.AppendLine($"=== {hdr.Name} ({hdr.Fields.Count}) ===");
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
        if (isOperationRunning || isPreparingTrueHD) {
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
        if (isOperationRunning || isPreparingTrueHD) {
            ShowError(S["OpRunning"]);
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active != null && active.Codec == Codec.TrueHD) {
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

    void StartStudioW12PresetExport() {
        if (isOperationRunning || isPreparingTrueHD) {
            ShowError(S["OpRunning"]);
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        StopPlayback();
        StudioExportPreset preset = (StudioExportPreset)Math.Clamp(studioW12PresetSelector?.SelectedIndex ?? 1, 0, 7);
        string dir = AndroidFileHelper.GetWorkingOutputDir(this, "Renderizados");
        string baseName = Path.GetFileNameWithoutExtension(LoadedFile?.Path ?? "master");
        string ext = StudioBatchExporter.GetDefaultExtension(preset);
        string outPath = Path.Combine(dir, $"{baseName}_{preset}{ext}");
        int n = 1;
        while (File.Exists(outPath)) {
            outPath = Path.Combine(dir, $"{baseName}_{preset}_{n++}{ext}");
        }

        StudioBatchExportJob job = new() {
            TrackName = baseName,
            Preset = preset,
            OutputPath = outPath,
            BitDepth = store.Force24Bit ? BitDepth.Int24 : BitDepth.Int16,
            TruePeakCeilingDbtp = store.ResolveTruePeakCeilingDbtp(),
            KaraokeVocalLevel = Math.Clamp(store.KaraokeVocalLevel, 0f, 1f),
            PreserveBackingVocals = store.KaraokePreserveBackingVocals,
            VocalDnaProfile = playerEngine?.VocalMixMatchStage?.ActiveDna,
            LosslessTwinProvider = playerEngine?.SceneHub?.ActiveLosslessReference,
            HoaOrder = 3
        };

        RunBackgroundOperation(token => {
            RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
            StudioBatchExportResult res = StudioBatchExporter.ExportSingle(
                active,
                job,
                p => RunOnUiThread(() => SetConvertStatus(preset.ToString(), p)),
                token);
            if (!res.Succeeded) {
                throw new InvalidOperationException(res.ErrorMessage);
            }
            RunOnUiThread(() => SetConvertStatus(
                string.Format(S["StudioW12DoneFormat"], Path.GetFileName(res.OutputPath), res.IntegratedLufs, res.MaxTruePeakDbtp),
                1.0));
        }, outPath);
    }

    string BuildDefaultExportPath(CavernizeTrack track) {
        string dir = AndroidFileHelper.GetWorkingOutputDir(this, "Renderizados");
        string baseName = Path.GetFileNameWithoutExtension(LoadedFile?.Path ?? "render");
        int selIdx = containerSelector != null ? Math.Clamp(containerSelector.SelectedIndex, 0, Math.Max(0, availableContainerExts.Count - 1)) : 0;
        string ext = availableContainerExts.Count > 0 ? availableContainerExts[selIdx] : ".wav";
        string candidate = Path.Combine(dir, $"{baseName}_cavernized{ext}");
        int n = 1;
        while (File.Exists(candidate)) {
            candidate = Path.Combine(dir, $"{baseName}_cavernized_{n++}{ext}");
        }
        return candidate;
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
                TranscodeTask(target, transcoder, activeCts?.Token ?? CancellationToken.None);
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
        ExecuteTranscodeBody(effective, transcoder, token);
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
            merger.SetTrackName($"Cavern {RenderTarget.Name} render");
            if (writer.ChannelCount > 8) {
                merger.Allow8PlusChannels();
            }
            merger.MakeSafe(finalName);
            if (!merger.Merge(ffmpeg, finalName)) {
                string reason = string.IsNullOrWhiteSpace(merger.LastError) ? "Unknown error" : merger.LastError;
                AppLogger.Error($"RenderTask merge failed for '{finalName}': {reason}");
                throw new IOException($"FFmpeg merge failed.\n{reason}");
            }
        }

        if (!string.IsNullOrEmpty(finalName)) {
            AndroidFileHelper.PublishToDownloads(this, finalName, "Renderizados");
            lastRenderedFilePath = finalName;
            AppLogger.Info($"Render finished successfully: '{finalName}'");
            RunOnUiThread(() => {
                SetConvertStatus(string.Format(S["ExportDone"], finalName), 1);
                shareExportBtn.Visibility = ViewStates.Visible;
                playExportBtn.Visibility = ViewStates.Visible;
            });
        } else {
            RunOnUiThread(() => SetConvertStatus(S["ReportDone"], 1));
        }
    }

    void TranscodeTask(CavernizeTrack target, EnvironmentWriter writer, CancellationToken token) {
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
        ExecuteTranscodeBody(target, writer, token);
    }

    void ExecuteTranscodeBody(CavernizeTrack target, EnvironmentWriter writer, CancellationToken token) {
        RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
        RenderStats stats = writer is BroadcastWaveFormatWriter bwf
            ? WriteTranscodeBwf(target, bwf, token)
            : WriteTranscodeGeneric(target, writer, token);
        report.Generate(stats);
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
        bool useDistSim = applyRoomToExportCheck != null && applyRoomToExportCheck.Checked && playerEngine.DistanceSimulation;
        float prevListenerNormalizer = environment.Listener.Normalizer;

        if (virtualizerState && useDistSim) {
            new VirtualizerFilter().SetLayout();
            environment.Listener.Normalizer = 0f;
            normalizer = new Normalizer(true) {
                SampleRate = environment.Listener.SampleRate,
                Channels = renderTarget.OutputChannels
            };
        } else if (virtualizerState || store.SpeakerVirtualizer) {
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
                    if (virtualizerState && useDistSim && virtualizer == null) {
                        int ch = Listener.Channels.Length;
                        int outCh = renderTarget.OutputChannels;
                        int framesInBlock = cachePosition / ch;
                        float[] stereo = new float[framesInBlock * outCh];
                        for (int s = 0; s < framesInBlock; s++) {
                            for (int c = 0; c < outCh; c++) {
                                stereo[s * outCh + c] = writeCache[s * ch + c];
                            }
                        }
                        normalizer.Process(stereo);
                        writer?.WriteBlock(stereo, 0, stereo.Length);
                    } else if (virtualizer == null) {
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
                    RunOnUiThread(() => SetConvertStatus($"Render: {progress:0.0%} ({speed:0.0}x)", progress));
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
                RunOnUiThread(() => SetConvertStatus($"Transcode: {p:0.0%}", p));
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
                RunOnUiThread(() => SetConvertStatus($"Transcode ADM: {p:0.0%}", p));
                nextUi = rendered + 50000;
            }
        }
        writer.FinalFeedback = p => RunOnUiThread(() => SetConvertStatus($"Finalizing ADM: {p:0.0%}", progressSplit + p * (1 - progressSplit)));
        writer.FinalFeedbackStart = progressSplit;
        writer.Dispose();
        return stats;
    }

    void RunBackgroundOperation(Action<CancellationToken> work, string singleOutputPath) {
        activeCts?.Dispose();
        activeCts = new CancellationTokenSource();
        CancellationToken token = activeCts.Token;

        isOperationRunning = true;
        SetRunningUiState(true);
        Task.Run(() => {
            try {
                work(token);
                if (!string.IsNullOrEmpty(singleOutputPath) && File.Exists(singleOutputPath)) {
                    AndroidFileHelper.PublishToDownloads(this, singleOutputPath, "Renderizados");
                    lastRenderedFilePath = singleOutputPath;
                    RunOnUiThread(() => {
                        shareExportBtn.Visibility = ViewStates.Visible;
                        playExportBtn.Visibility = ViewStates.Visible;
                    });
                }
            } catch (OperationCanceledException) {
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
        if (cancelRenderBtn != null) cancelRenderBtn.Enabled = running;
        if (extractBtn != null) extractBtn.Enabled = !running;
        if (cancelExtractBtn != null) cancelExtractBtn.Enabled = running;
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
            : "*/*");
        share.PutExtra(Intent.ExtraStream, uri);
        share.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(Intent.CreateChooser(share, S["ShareFile"]));
    }
}
