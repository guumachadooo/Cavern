using System.Numerics;
using System.Text;

using Android.App;
using Android.Content;
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
    Button selectTargetBtn, wiringBtn, renderBtn, cancelRenderBtn, shareExportBtn, playExportBtn;
    LayoutDiagramView layoutDiagram;
    TextView converterTrackInfoText, convertStatusText;
    Spinner exportFormatSpinner, containerSpinner;
    CheckBox applyRoomToExportCheck;
    ProgressBar convertProgressBar;

    List<string> availableContainerExts = [".wav"];
    int blockSize;
    const string waveExtension = ".wav";
    const int secondFrame = 2 * 1536;
    const double progressSplit = .95;

    LinearLayout BuildConverterTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(12), Dp(6), Dp(12), Dp(16));

        // 1. System & RenderTarget Card
        LinearLayout sysCard = Card(S["SystemTitle"]);
        LinearLayout sysRow = new(this) { Orientation = Orientation.Horizontal };
        sysRow.SetGravity(GravityFlags.CenterVertical);

        LinearLayout sysLeft = new(this) { Orientation = Orientation.Vertical };
        sysLeft.AddView(Label(S["RenderTarget"], 12f, ColMuted));
        selectTargetBtn = SecondaryButton(RenderTarget?.Name ?? S["SelectTarget"], ShowRenderTargetDialog);
        sysLeft.AddView(selectTargetBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(42)) { TopMargin = Dp(4), BottomMargin = Dp(6) });
        wiringBtn = PillButton(S["DisplayWiring"], DisplayWiringDialog);
        sysLeft.AddView(wiringBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(38)));
        sysRow.AddView(sysLeft, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(10) });

        layoutDiagram = new LayoutDiagramView(this);
        sysRow.AddView(layoutDiagram);
        sysCard.AddView(sysRow);
        page.AddView(sysCard);

        // 2. Content & Export Card
        LinearLayout expCard = Card("Export");
        converterTrackInfoText = Label(S["NoTrack"], 12f, ColMuted);
        converterTrackInfoText.SetPadding(0, 0, 0, Dp(8));
        expCard.AddView(converterTrackInfoText);
        AddDivider(expCard);

        expCard.AddView(Label(S["ExportFormat"], 12f, ColMuted));
        exportFormatSpinner = new Spinner(this);
        ExportFormat[] formats = ExportFormat.GetFormats();
        ArrayAdapter<string> fmtAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, formats.Select(f => f.Description).ToArray());
        fmtAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        exportFormatSpinner.Adapter = fmtAdapter;
        exportFormatSpinner.SetSelection(Math.Clamp(Array.IndexOf(formats, ExportFormat), 0, formats.Length - 1));
        exportFormatSpinner.ItemSelected += (_, e) => {
            ExportFormat[] curFormats = ExportFormat.GetFormats();
            if (e.Position >= 0 && e.Position < curFormats.Length) {
                ExportFormat = curFormats[e.Position];
                store.ExportFormatIndex = e.Position;
                RefreshContainerOptions();
            }
        };
        expCard.AddView(exportFormatSpinner);

        AddDivider(expCard);
        expCard.AddView(Label(S["Container"], 12f, ColMuted));
        containerSpinner = new Spinner(this);
        expCard.AddView(containerSpinner);

        AddDivider(expCard);
        applyRoomToExportCheck = new CheckBox(this) {
            Text = S["ApplyRoomToExport"],
            Checked = false,
            TextSize = 12.5f
        };
        applyRoomToExportCheck.SetTextColor(ColText);
        applyRoomToExportCheck.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ColAccent);
        expCard.AddView(applyRoomToExportCheck);

        // Action Buttons
        LinearLayout actRow1 = new(this) { Orientation = Orientation.Horizontal };
        actRow1.SetPadding(0, Dp(10), 0, Dp(6));
        renderBtn = PrimaryButton(S["RenderBtn"], () => StartSingleRender());
        cancelRenderBtn = PillButton(S["CancelBtn"], () => activeCts?.Cancel());
        cancelRenderBtn.Enabled = false;
        actRow1.AddView(renderBtn, new LinearLayout.LayoutParams(0, Dp(44), 2f) { RightMargin = Dp(6) });
        actRow1.AddView(cancelRenderBtn, new LinearLayout.LayoutParams(0, Dp(44), 1f));
        expCard.AddView(actRow1);

        Button queueAddBtn = PillButton(S["AddQueueBtn"], AddCurrentToQueue);
        expCard.AddView(queueAddBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(40)) { BottomMargin = Dp(8) });

        LinearLayout infoBtns = new(this) { Orientation = Orientation.Horizontal };
        Button metaBtn = PillButton(S["ShowMetaBtn"], ShowMetadataDialog);
        Button repBtn = PillButton(S["ShowReportBtn"], ShowPostRenderReportDialog);
        infoBtns.AddView(metaBtn, new LinearLayout.LayoutParams(0, Dp(38), 1f) { RightMargin = Dp(6) });
        infoBtns.AddView(repBtn, new LinearLayout.LayoutParams(0, Dp(38), 1f));
        expCard.AddView(infoBtns);

        // Progress & Result
        convertProgressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0
        };
        OneUiTheme.StyleProgressBar(this, convertProgressBar);
        LinearLayout.LayoutParams pbLp = new(ViewGroup.LayoutParams.MatchParent, Dp(8)) { TopMargin = Dp(10) };
        expCard.AddView(convertProgressBar, pbLp);

        convertStatusText = Label(S["Ready"], 12f, ColCyan);
        convertStatusText.SetPadding(0, Dp(6), 0, Dp(6));
        expCard.AddView(convertStatusText);

        LinearLayout postBtns = new(this) { Orientation = Orientation.Horizontal };
        shareExportBtn = PillButton(S["ShareFile"], () => ShareLocalFile(lastRenderedFilePath));
        playExportBtn = PillButton(S["OpenPlayer"], () => {
            if (!string.IsNullOrEmpty(lastRenderedFilePath) && File.Exists(lastRenderedFilePath)) {
                OpenContent(lastRenderedFilePath);
                SwitchTab(0);
            }
        }, active: true);
        shareExportBtn.Visibility = ViewStates.Gone;
        playExportBtn.Visibility = ViewStates.Gone;
        postBtns.AddView(shareExportBtn, new LinearLayout.LayoutParams(0, Dp(38), 1f) { RightMargin = Dp(6) });
        postBtns.AddView(playExportBtn, new LinearLayout.LayoutParams(0, Dp(38), 1f));
        expCard.AddView(postBtns);

        page.AddView(expCard);
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
        RefreshConverterTrackInfo();
        RefreshContainerOptions();
    }

    void RefreshConverterTrackInfo() {
        if (converterTrackInfoText == null) {
            return;
        }
        if (selectedTrack == null) {
            converterTrackInfoText.Text = S["NoTrack"];
            return;
        }
        StringBuilder sb = new(selectedTrack.FormatHeader);
        foreach (var (prop, val) in selectedTrack.Details) {
            sb.Append('\n').Append(prop).Append(": ").Append(val);
        }
        converterTrackInfoText.Text = sb.ToString();
    }

    void RefreshContainerOptions() {
        if (containerSpinner == null || ExportFormat == null) {
            return;
        }
        List<string> labels = [];
        List<string> exts = [];

        if (selectedTrack != null) {
            try {
                string filter = MergeToContainer.GetPossibleContainers(selectedTrack, ExportFormat.Codec, ffmpeg);
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
            exts.Add(ExportFormat.Codec == Codec.LimitlessAudio ? ".laf" :
                     ExportFormat.Codec == Codec.DAMF ? ".atmos" : ".wav");
            labels.Add(exts[0]);
        }

        availableContainerExts = exts;
        ArrayAdapter<string> adapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, labels.ToArray());
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        containerSpinner.Adapter = adapter;
    }

    void ShowRenderTargetDialog() {
        ScrollView sv = new(this);
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        list.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));

        AlertDialog dialog = null;
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
            hdr.SetPadding(0, Dp(8), 0, Dp(4));
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
                list.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(38)) { BottomMargin = Dp(4) });
            }
        }

        AddCategory(S["CatPC"], pc);
        AddCategory(S["CatMatrix"], matrix);
        AddCategory(S["CatMulti"], multi);
        sv.AddView(list);

        dialog = new AlertDialog.Builder(this)
            .SetTitle(S["RenderTarget"])
            .SetView(sv)
            .SetNegativeButton(S["CancelBtn"], (_, _) => { })
            .Create();
        dialog.Window?.SetBackgroundDrawable(OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceElevated, OneUiTheme.CardCornerDp, OneUiTheme.FloatingBorder, 1f));
        dialog.Show();
    }

    void DisplayWiringDialog() {
        if (RenderTarget == null) {
            return;
        }
        ReferenceChannel[] channels = RenderTarget.GetNameMappedChannels();
        ChannelPrototype[] prototypes = ChannelPrototype.Get(channels);
        if (channels.Length > 8) {
            string order = string.Join("", prototypes.Select(x => "\n- " + x.Name));
            ShowMessageDialog(S["WrGui"], string.Format(S["Over8"], order));
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
        ShowMessageDialog(S["WrGui"], output.ToString());
    }

    void ShowMetadataDialog() {
        if (selectedTrack == null) {
            ShowError(S["NoTrack"]);
            return;
        }
        ReadableMetadata meta = selectedTrack.GetMetadata();
        if (meta == null) {
            ShowError("No detailed codec metadata available for this track.");
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
        ShowMessageDialog(S["ShowMetaBtn"], sb.ToString());
    }

    void ShowPostRenderReportDialog() {
        ShowMessageDialog(S["ShowReportBtn"], report?.Report ?? "");
    }

    // ICavernizeApp Render Pipeline
    void PreRender() {
        if (isOperationRunning) {
            throw new ConcurrencyException(S["OpRunning"]);
        }
        if (selectedTrack == null || !selectedTrack.Supported) {
            throw new TrackException(S["NoTrack"]);
        }
        bool needsFFmpeg = !string.IsNullOrEmpty(ExportFormat.FFName) &&
            ExportFormat.Codec != Codec.PCM_Float && ExportFormat.Codec != Codec.PCM_LE;
        if (needsFFmpeg && !ffmpeg.Found) {
            throw new TrackException(string.Format(S["FFmpegNeeded"], ExportFormat.Codec));
        }
        AttachToListener();
    }

    void AttachToListener() {
        store.ApplyGlobals();
        environment.AttachToListener(selectedTrack);
        if (applyRoomToExportCheck != null && applyRoomToExportCheck.Checked) {
            environment.Listener.Position = playerEngine.ListenerPosition;
            environment.Listener.Rotation = playerEngine.ListenerRotation;
            foreach (Source s in selectedTrack.Renderer.Objects) {
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

        CavernizeTrack target = selectedTrack;
        if (!store.ReportOnlyMode) {
            if (string.IsNullOrEmpty(path)) {
                path = BuildDefaultExportPath(target);
            }
            try {
                return CreateRenderAction(path);
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
        if (isOperationRunning) {
            ShowError(S["OpRunning"]);
            return;
        }
        StopPlayback();
        try {
            PreRender();
        } catch (Exception e) {
            ShowError(e.Message);
            return;
        }

        string outPath = store.ReportOnlyMode ? null : BuildDefaultExportPath(selectedTrack);
        Action task = store.ReportOnlyMode
            ? () => RenderTask(selectedTrack, null, null, activeCts.Token)
            : CreateRenderAction(outPath);

        RunBackgroundOperation(_ => task(), outPath);
    }

    string BuildDefaultExportPath(CavernizeTrack track) {
        string dir = AndroidFileHelper.GetWorkingOutputDir(this);
        string baseName = Path.GetFileNameWithoutExtension(LoadedFile?.Path ?? "render");
        int selIdx = containerSpinner != null ? Math.Clamp(containerSpinner.SelectedItemPosition, 0, availableContainerExts.Count - 1) : 0;
        string ext = availableContainerExts[selIdx];
        string candidate = Path.Combine(dir, $"{baseName}_cavernized{ext}");
        int n = 1;
        while (File.Exists(candidate)) {
            candidate = Path.Combine(dir, $"{baseName}_cavernized_{n++}{ext}");
        }
        return candidate;
    }

    Action CreateRenderAction(string path) {
        CavernizeTrack target = selectedTrack;
        Codec codec = ExportFormat.Codec;
        BitDepth bits = codec == Codec.PCM_Float ? BitDepth.Float32 : store.Force24Bit ? BitDepth.Int24 : BitDepth.Int16;

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

    void RenderTask(CavernizeTrack target, AudioWriter writer, string finalName, CancellationToken token) {
        using ExternalConverterHandler external = CreateExternalHandler(target, 0);
        if (external.Failed) {
            writer?.Dispose();
            return;
        }

        RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
        RenderStats stats = WriteRender(target, writer, RenderTarget, token);
        report.Generate(stats);

        if (writer is RIFFWaveWriter && !string.IsNullOrEmpty(finalName) && !finalName.EndsWith(waveExtension, StringComparison.OrdinalIgnoreCase)) {
            RunOnUiThread(() => SetConvertStatus("Merging with FFmpeg...", 0.96));
            string exportedAudio = finalName[..^4] + waveExtension;
            MergeToContainer merger = new(LoadedFile.Path, exportedAudio, ExportFormat.FFName);
            merger.AddArguments(RenderingSettings.MergeArguments);
            merger.SetTrackName($"Cavern {RenderTarget.Name} render");
            if (writer.ChannelCount > 8) {
                merger.Allow8PlusChannels();
            }
            merger.MakeSafe(finalName);
            if (!merger.Merge(ffmpeg, finalName)) {
                throw new IOException("FFmpeg merge failed.");
            }
        }

        if (!string.IsNullOrEmpty(finalName)) {
            AndroidFileHelper.PublishToDownloads(this, finalName);
            lastRenderedFilePath = finalName;
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
        using ExternalConverterHandler external = CreateExternalHandler(target, writer is DolbyAtmosBWFWriter ? 10 : 0);
        if (external.Failed) {
            writer.Dispose();
            return;
        }

        RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
        RenderStats stats = writer is BroadcastWaveFormatWriter bwf
            ? WriteTranscodeBwf(target, bwf, token)
            : WriteTranscodeGeneric(target, writer, token);
        report.Generate(stats);
    }

    ExternalConverterHandler CreateExternalHandler(CavernizeTrack target, int keepFirstSources) {
        AndroidLicencePrompt prompt = new((desc, text) => {
            bool accepted = false;
            using ManualResetEventSlim mre = new(false);
            RunOnUiThread(() => {
                new AlertDialog.Builder(this)
                    .SetTitle("External Decoder Licence")
                    .SetMessage($"{desc}\n\n{text}")
                    .SetPositiveButton("Accept", (_, _) => { accepted = true; mre.Set(); })
                    .SetNegativeButton("Decline", (_, _) => { accepted = false; mre.Set(); })
                    .SetCancelable(false)
                    .Show();
            });
            mre.Wait();
            return accepted;
        });

        ExternalConverterHandler external = new(target, prompt,
            p => RunOnUiThread(() => SetConvertStatus(convertStatusText.Text, p)),
            msg => RunOnUiThread(() => SetConvertStatus(msg, convertProgressBar.Progress / 1000.0)),
            RunOnUiThreadSync);
        if (!external.Failed) {
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

        if (virtualizerState && useDistSim) {
            // Keep Listener.HeadphoneVirtualizer = true so Source.Distancer runs, then extract L/R
            new VirtualizerFilter().SetLayout();
            normalizer = new Normalizer(true) {
                decayFactor = 10f * updateRate / environment.Listener.SampleRate
            };
        } else if (virtualizerState || store.SpeakerVirtualizer) {
            Listener.HeadphoneVirtualizer = false;
            virtualizer = new VirtualizerFilter();
            virtualizer.SetLayout();
            normalizer = new Normalizer(true) {
                decayFactor = 10f * updateRate / environment.Listener.SampleRate
            };
        }

        int cachePosition = 0;
        float[] writeCache = new float[blockSize / renderTarget.OutputChannels * Listener.Channels.Length];
        bool customPlayerMute = applyRoomToExportCheck != null && applyRoomToExportCheck.Checked;
        bool anySolo = customPlayerMute && trackSourceItems.Any(x => x.IsSolo);

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
                    virtualizer.Process(writeCache, environment.Listener.SampleRate);
                    normalizer.Process(writeCache);
                    writer?.WriteChannelLimitedBlock(writeCache, renderTarget.OutputChannels, Listener.Channels.Length, 0, cachePosition);
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

        writer?.Dispose();
        if (virtualizerState) {
            Listener.HeadphoneVirtualizer = true;
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
                    AndroidFileHelper.PublishToDownloads(this, singleOutputPath);
                    lastRenderedFilePath = singleOutputPath;
                    RunOnUiThread(() => {
                        shareExportBtn.Visibility = ViewStates.Visible;
                        playExportBtn.Visibility = ViewStates.Visible;
                    });
                }
            } catch (OperationCanceledException) {
                RunOnUiThread(() => SetConvertStatus("Cancelled.", 0));
            } catch (Exception ex) {
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
        share.SetType("*/*");
        share.PutExtra(Intent.ExtraStream, uri);
        share.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(Intent.CreateChooser(share, S["ShareFile"]));
    }
}
