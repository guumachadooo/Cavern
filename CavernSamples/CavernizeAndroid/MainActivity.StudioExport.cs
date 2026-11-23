using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.Format;
using Cavern.Format.Common;

using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    TextView studioRecStatusText;
    Button studioRecRecordBtn, studioRecPreviewBtn, studioRecDiscardBtn;
    LinearLayout studioRecActionRow;
    Java.Lang.Runnable studioRecTimerRunnable;
    string lastRecordedTrackSourcePath = "";

    OneUiInlineSelector studioBaseFormatSelector;
    TextView studioBaseFormatDescText;
    Switch studioOptAiUpmixSwitch, studioOptKaraokeSwitch, studioOptPerformanceSwitch;
    Switch studioOptLosslessTwinSwitch, studioOptHeadphonePersonalSwitch;
    TextView studioOptPerformanceHintText, studioOptTwinHintText;
    LinearLayout studioOptHeadphonePersonalRow;

    LinearLayout BuildStudioPerformanceRecorderCard() {
        LinearLayout card = Card(S["StudioRecCardTitle"], OneUiIconType.Microphone, OneUiTheme.FunctionalRed);

        TextView sub = Label(S["StudioRecCardSub"], 11.6f, OneUiTheme.TextSub);
        sub.SetPadding(0, 0, 0, Dp(8));
        card.AddView(sub);

        studioRecStatusText = Label(S["StudioRecStatusIdle"], 12.2f, OneUiTheme.TextSecondary);
        studioRecStatusText.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        studioRecStatusText.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceSubTile, 12f, OneUiTheme.BorderSubtle, 0.8f);
        card.AddView(studioRecStatusText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        studioRecRecordBtn = PrimaryButton(S["StudioRecStartBtn"], OnStudioRecordToggleClicked);
        card.AddView(studioRecRecordBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        studioRecActionRow = HRow();
        studioRecActionRow.Visibility = ViewStates.Gone;
        studioRecPreviewBtn = SecondaryButton(S["StudioRecPreviewBtn"], OnStudioPreviewToggleClicked);
        studioRecDiscardBtn = PillButton(S["StudioRecDiscardBtn"], OnStudioDiscardTakeClicked);
        studioRecActionRow.AddView(studioRecPreviewBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.3f) {
            RightMargin = Dp(8)
        });
        studioRecActionRow.AddView(studioRecDiscardBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.7f));
        card.AddView(studioRecActionRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });

        return card;
    }

    LinearLayout BuildStudioComposableExportCard() {
        LinearLayout card = Card(S["StudioExportBuilderTitle"], OneUiIconType.Sparkles, OneUiTheme.IconStudioExport);

        studioBaseFormatSelector = new OneUiInlineSelector(this, S["StudioExportFormatLabel"]);
        studioBaseFormatSelector.SetItems(new[] {
            S["StudioFmtHeadphone3D"],
            S["StudioFmtCinema714"],
            S["StudioFmtAtmosMaster"],
            S["StudioFmtEclipsa3D"],
            S["StudioFmtSpatial360"]
        }, store.StudioBaseFormatIndex);
        studioBaseFormatSelector.SelectionChanged += idx => {
            store.StudioBaseFormatIndex = idx;
            RefreshStudioW12ContextUi();
        };
        card.AddView(studioBaseFormatSelector);

        studioBaseFormatDescText = Label("", 11.4f, OneUiTheme.TextSub);
        studioBaseFormatDescText.SetPadding(0, Dp(5), 0, Dp(8));
        card.AddView(studioBaseFormatDescText);

        TextView featuresHeader = Label(S["StudioExportModulesHeader"], 12.2f, OneUiTheme.TextPrimary);
        featuresHeader.SetTypeface(null, TypefaceStyle.Bold);
        featuresHeader.SetPadding(0, Dp(2), 0, Dp(4));
        card.AddView(featuresHeader);

        studioOptAiUpmixSwitch = AddExplainedSwitchRow(
            card,
            S["StudioOptAiTitle"],
            S["StudioOptAiDesc"],
            store.StudioOptAiUpmix,
            on => {
                store.StudioOptAiUpmix = on;
                RefreshStudioW12ContextUi();
            },
            out _);

        studioOptKaraokeSwitch = AddExplainedSwitchRow(
            card,
            S["StudioOptKaraokeTitle"],
            S["StudioOptKaraokeDesc"],
            store.StudioOptKaraoke,
            on => {
                store.StudioOptKaraoke = on;
                RefreshStudioW12ContextUi();
            },
            out _);

        studioW12KaraokeContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.StudioOptKaraoke ? ViewStates.Visible : ViewStates.Gone
        };
        studioW12KaraokeContainer.SetPadding(Dp(6), 0, Dp(4), Dp(6));
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
        card.AddView(studioW12KaraokeContainer);

        studioOptPerformanceSwitch = AddExplainedSwitchRow(
            card,
            S["StudioOptPerfTitle"],
            S["StudioOptPerfDesc"],
            store.StudioOptPerformance,
            on => {
                store.StudioOptPerformance = on;
                RefreshStudioW12ContextUi();
            },
            out studioOptPerformanceHintText);

        studioOptLosslessTwinSwitch = AddExplainedSwitchRow(
            card,
            S["StudioOptTwinTitle"],
            S["StudioOptTwinDesc"],
            store.StudioOptLosslessTwin,
            on => {
                store.StudioOptLosslessTwin = on;
                RefreshStudioW12ContextUi();
            },
            out studioOptTwinHintText);

        studioOptHeadphonePersonalRow = new LinearLayout(this) { Orientation = Orientation.Vertical };
        studioOptHeadphonePersonalSwitch = AddExplainedSwitchRow(
            studioOptHeadphonePersonalRow,
            S["StudioOptHeadphoneTitle"],
            S["StudioOptHeadphoneDesc"],
            store.StudioOptPersonalizedHeadphone,
            on => {
                store.StudioOptPersonalizedHeadphone = on;
                RefreshStudioW12ContextUi();
            },
            out _);
        card.AddView(studioOptHeadphonePersonalRow);

        studioW12ContextText = Label("", 11.4f, OneUiTheme.TextSub);
        studioW12ContextText.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
        studioW12ContextText.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceSubTile, 12f, OneUiTheme.BorderSubtle, 0.8f);
        card.AddView(studioW12ContextText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });

        studioW12BatchBtn = PillButton(S["StudioW12BatchBtn"], ShowStudioW12BatchDialog);
        card.AddView(studioW12BatchBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });

        return card;
    }

    Switch AddExplainedSwitchRow(
        LinearLayout parent,
        string title,
        string description,
        bool initial,
        Action<bool> onChanged,
        out TextView descView) {
        LinearLayout wrap = new(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(0, Dp(4), 0, Dp(6));

        Switch sw = AddSwitchRow(wrap, title, initial, onChanged);
        descView = Label(description, 11.2f, OneUiTheme.TextSub);
        descView.SetPadding(0, Dp(1), Dp(46), 0);
        wrap.AddView(descView);

        parent.AddView(wrap);
        return sw;
    }

    StudioComposableExportOptions BuildCurrentComposableOptions() {
        int fmtIdx = Math.Clamp(studioBaseFormatSelector?.SelectedIndex ?? store?.StudioBaseFormatIndex ?? 0, 0, 4);
        PerformanceRecordingTake take = playerEngine?.LiveMicBridge?.LatestRecordedTake;
        bool includePerf = store?.StudioOptPerformance ?? false;
        return new StudioComposableExportOptions {
            BaseFormat = (StudioBaseOutputFormat)fmtIdx,
            EnableAiSpatialUpmix = store?.StudioOptAiUpmix ?? false,
            EnableKaraokeInstrumental = store?.StudioOptKaraoke ?? false,
            IncludeRecordedPerformance = includePerf,
            EnableLosslessTwinRestoration = store?.StudioOptLosslessTwin ?? false,
            ApplyPersonalizedHeadphonePost = store?.StudioOptPersonalizedHeadphone ?? true,
            RecordedPerformanceStartTrackSample = includePerf ? (take?.StartTrackSample ?? 0L) : 0L,
            RecordedPerformanceSampleRate = includePerf ? (take?.SampleRate ?? 48000) : 48000
        };
    }

    string GetStudioBaseFormatLabel(StudioBaseOutputFormat fmt) => fmt switch {
        StudioBaseOutputFormat.Headphone3D => S["StudioFmtHeadphone3D"],
        StudioBaseOutputFormat.Cinema714 => S["StudioFmtCinema714"],
        StudioBaseOutputFormat.DolbyAtmosAdm => S["StudioFmtAtmosMaster"],
        StudioBaseOutputFormat.EclipsaIamf => S["StudioFmtEclipsa3D"],
        StudioBaseOutputFormat.Ambisonics360 => S["StudioFmtSpatial360"],
        _ => fmt.ToString()
    };

    string GetStudioBaseFormatDescription(StudioBaseOutputFormat fmt) => fmt switch {
        StudioBaseOutputFormat.Headphone3D => S["StudioFmtHeadphone3DDesc"],
        StudioBaseOutputFormat.Cinema714 => S["StudioFmtCinema714Desc"],
        StudioBaseOutputFormat.DolbyAtmosAdm => S["StudioFmtAtmosMasterDesc"],
        StudioBaseOutputFormat.EclipsaIamf => S["StudioFmtEclipsa3DDesc"],
        StudioBaseOutputFormat.Ambisonics360 => S["StudioFmtSpatial360Desc"],
        _ => ""
    };

    void OnStudioRecordToggleClicked() {
        if (uiDestroyed || playerEngine == null) {
            return;
        }
        LiveMicrophoneBridge mic = playerEngine.LiveMicBridge;
        if (mic.IsRecordingPerformance) {
            StopStudioPerformanceRecording(autoSelectInExport: true);
            return;
        }

        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        if (active.Codec == Codec.TrueHD) {
            pendingTrueHDStudioTrack = selectedTrack;
            pendingPostTrueHDStudioAction = OnStudioRecordToggleClicked;
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }
        if (!RequestRecordAudioPermissionIfNeeded()) {
            Toast.MakeText(this, S["StudioRecGrantMicToast"], ToastLength.Long).Show();
            return;
        }

        // Stop any active take preview before recording a fresh take
        playerEngine.SetPerformancePreviewTake(null, enabled: false);

        if (!(audioOutput?.EnsureStudioRecordingCapture(true) ?? false)) {
            ShowError(S["StudioRecMicFailedToast"]);
            return;
        }

        if (!playerEngine.IsPlaying) {
            if (playerEngine.TotalSeconds > 0 && playerEngine.CurrentSeconds >= playerEngine.TotalSeconds - 0.1) {
                playerEngine.Seek(0);
            }
            playerEngine.IsPlaying = true;
            audioOutput?.Start();
            SyncPlaybackUi(true, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
            mediaNotification?.UpdatePlaybackState(true, playerEngine.CurrentSeconds, showNotification: true);
        }

        int captureSr = mic.SampleRate >= 8000 ? mic.SampleRate : playerEngine.OutputSampleRate;
        mic.StartPerformanceRecording(captureSr, playerEngine.CurrentTrackSample);
        StartStudioRecUiTimer();
        RefreshStudioRecorderUi();
        Toast.MakeText(this, S["StudioRecStartedToast"], ToastLength.Short).Show();
    }

    void StopStudioPerformanceRecording(bool autoSelectInExport) {
        StopStudioRecUiTimer();
        if (playerEngine == null) {
            return;
        }
        LiveMicrophoneBridge mic = playerEngine.LiveMicBridge;
        if (!mic.IsRecordingPerformance) {
            return;
        }
        PerformanceRecordingTake take = mic.StopPerformanceRecording();
        audioOutput?.EnsureStudioRecordingCapture(false);

        if (take != null && take.HasAudio) {
            if (autoSelectInExport && store != null) {
                store.StudioOptPerformance = true;
                if (studioOptPerformanceSwitch != null && !studioOptPerformanceSwitch.Checked) {
                    studioOptPerformanceSwitch.Checked = true;
                }
            }
            Toast.MakeText(
                this,
                string.Format(S["StudioRecStoppedToastFmt"], FormatMmSs(take.DurationSeconds)),
                ToastLength.Short).Show();
        }
        RefreshStudioRecorderUi();
        RefreshStudioW12ContextUi();
    }

    void OnStudioPreviewToggleClicked() {
        if (uiDestroyed || playerEngine == null) {
            return;
        }
        PerformanceRecordingTake take = playerEngine.LiveMicBridge.LatestRecordedTake;
        if (take == null || !take.HasAudio) {
            Toast.MakeText(this, S["StudioRecNeedTakeToast"], ToastLength.Short).Show();
            return;
        }

        if (playerEngine.IsPreviewingRecordedPerformance && playerEngine.IsPlaying) {
            playerEngine.SetPerformancePreviewTake(null, enabled: false);
            playerEngine.IsPlaying = false;
            audioOutput?.Pause();
            SyncPlaybackUi(false, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
            mediaNotification?.UpdatePlaybackState(false, playerEngine.CurrentSeconds, showNotification: true);
            RefreshStudioRecorderUi();
            return;
        }

        int trackSr = GetActiveTrack()?.SampleRate is int sr && sr > 0
            ? sr
            : (take.SampleRate > 0 ? take.SampleRate : 48000);
        double startSec = (double)take.StartTrackSample / trackSr;
        if (playerEngine.TotalSeconds > 0) {
            startSec = Math.Clamp(startSec, 0.0, Math.Max(0.0, playerEngine.TotalSeconds - 0.1));
        }
        playerEngine.Seek(startSec);
        playerEngine.SetPerformancePreviewTake(take, enabled: true);
        playerEngine.IsPlaying = true;
        audioOutput?.Start();
        SyncPlaybackUi(true, startSec, playerEngine.TotalSeconds);
        mediaNotification?.UpdatePlaybackState(true, startSec, showNotification: true);
        RefreshStudioRecorderUi();
        Toast.MakeText(this, S["StudioRecPreviewingToast"], ToastLength.Short).Show();
    }

    void OnStudioDiscardTakeClicked() {
        if (playerEngine == null) {
            return;
        }
        StopStudioPerformanceRecording(autoSelectInExport: false);
        playerEngine.SetPerformancePreviewTake(null, enabled: false);
        playerEngine.LiveMicBridge.ClearRecordedPerformance();
        if (store != null && store.StudioOptPerformance) {
            store.StudioOptPerformance = false;
            if (studioOptPerformanceSwitch != null && studioOptPerformanceSwitch.Checked) {
                studioOptPerformanceSwitch.Checked = false;
            }
        }
        RefreshStudioRecorderUi();
        RefreshStudioW12ContextUi();
        Toast.MakeText(this, S["StudioRecDiscardedToast"], ToastLength.Short).Show();
    }

    void StartStudioRecUiTimer() {
        StopStudioRecUiTimer();
        studioRecTimerRunnable = new Java.Lang.Runnable(() => {
            if (uiDestroyed || playerEngine == null || !playerEngine.LiveMicBridge.IsRecordingPerformance) {
                return;
            }
            RefreshStudioRecorderUi();
            uiHandler?.PostDelayed(studioRecTimerRunnable, 200);
        });
        uiHandler?.PostDelayed(studioRecTimerRunnable, 200);
    }

    void StopStudioRecUiTimer() {
        if (studioRecTimerRunnable != null) {
            uiHandler?.RemoveCallbacks(studioRecTimerRunnable);
            studioRecTimerRunnable = null;
        }
    }

    void RefreshStudioRecorderUi() {
        if (uiDestroyed || studioRecStatusText == null || playerEngine == null) {
            return;
        }
        LiveMicrophoneBridge mic = playerEngine.LiveMicBridge;
        bool isRec = mic.IsRecordingPerformance;
        PerformanceRecordingTake take = mic.LatestRecordedTake;
        bool hasTake = take != null && take.HasAudio;
        bool isPreviewing = playerEngine.IsPreviewingRecordedPerformance && playerEngine.IsPlaying;

        if (isRec) {
            string dur = FormatMmSs(mic.RecordingDurationSeconds);
            studioRecStatusText.Text = string.Format(S["StudioRecStatusRecordingFmt"], dur, mic.PeakDbfs);
            studioRecStatusText.SetTextColor(OneUiTheme.FunctionalRed);
            studioRecStatusText.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalRed, 32), 12f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalRed, 150), 1f);
            if (studioRecRecordBtn != null) {
                studioRecRecordBtn.Text = S["StudioRecStopBtn"];
                studioRecRecordBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.FunctionalRed, OneUiTheme.PillCornerDp);
            }
            if (studioRecActionRow != null) {
                studioRecActionRow.Visibility = ViewStates.Gone;
            }
        } else if (hasTake) {
            string dur = FormatMmSs(take.DurationSeconds);
            studioRecStatusText.Text = isPreviewing
                ? string.Format(S["StudioRecStatusPreviewingFmt"], dur)
                : string.Format(S["StudioRecStatusReadyFmt"], dur, take.PeakDbfs);
            studioRecStatusText.SetTextColor(isPreviewing ? OneUiTheme.IconSystemInfo : OneUiTheme.FunctionalGreen);
            Color tint = isPreviewing ? OneUiTheme.IconSystemInfo : OneUiTheme.FunctionalGreen;
            studioRecStatusText.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(tint, 26), 12f, OneUiTheme.WithAlpha(tint, 130), 1f);
            if (studioRecRecordBtn != null) {
                studioRecRecordBtn.Text = S["StudioRecReRecordBtn"];
                studioRecRecordBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.Primary, OneUiTheme.PillCornerDp);
            }
            if (studioRecPreviewBtn != null) {
                studioRecPreviewBtn.Text = isPreviewing ? S["StudioRecStopPreviewBtn"] : S["StudioRecPreviewBtn"];
            }
            if (studioRecActionRow != null) {
                studioRecActionRow.Visibility = ViewStates.Visible;
            }
        } else {
            studioRecStatusText.Text = S["StudioRecStatusIdle"];
            studioRecStatusText.SetTextColor(OneUiTheme.TextSecondary);
            studioRecStatusText.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.SurfaceSubTile, 12f, OneUiTheme.BorderSubtle, 0.8f);
            if (studioRecRecordBtn != null) {
                studioRecRecordBtn.Text = S["StudioRecStartBtn"];
                studioRecRecordBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.Primary, OneUiTheme.PillCornerDp);
            }
            if (studioRecActionRow != null) {
                studioRecActionRow.Visibility = ViewStates.Gone;
            }
        }
    }

    void RefreshStudioComposableExportUi() {
        if (uiDestroyed || store == null || studioBaseFormatSelector == null) {
            return;
        }
        StudioComposableExportOptions opts = BuildCurrentComposableOptions();

        if (studioBaseFormatDescText != null) {
            studioBaseFormatDescText.Text = GetStudioBaseFormatDescription(opts.BaseFormat);
        }
        if (studioW12KaraokeContainer != null) {
            studioW12KaraokeContainer.Visibility = opts.EnableKaraokeInstrumental ? ViewStates.Visible : ViewStates.Gone;
        }
        int vocPct = Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100);
        if (studioW12KaraokeBar != null && studioW12KaraokeBar.Progress != vocPct) {
            studioW12KaraokeBar.Progress = vocPct;
            studioW12KaraokeBar.Invalidate();
        }
        if (studioOptHeadphonePersonalRow != null) {
            studioOptHeadphonePersonalRow.Visibility = opts.BaseFormat == StudioBaseOutputFormat.Headphone3D
                ? ViewStates.Visible
                : ViewStates.Gone;
        }

        PerformanceRecordingTake take = playerEngine?.LiveMicBridge?.LatestRecordedTake;
        bool hasTake = take != null && take.HasAudio;
        if (studioOptPerformanceHintText != null) {
            if (hasTake) {
                studioOptPerformanceHintText.Text = string.Format(S["StudioOptPerfReadyDescFmt"], FormatMmSs(take.DurationSeconds));
                studioOptPerformanceHintText.SetTextColor(OneUiTheme.FunctionalGreen);
            } else if (opts.IncludeRecordedPerformance) {
                studioOptPerformanceHintText.Text = S["StudioOptPerfMissingDesc"];
                studioOptPerformanceHintText.SetTextColor(OneUiTheme.FunctionalOrange);
            } else {
                studioOptPerformanceHintText.Text = S["StudioOptPerfDesc"];
                studioOptPerformanceHintText.SetTextColor(OneUiTheme.TextSub);
            }
        }

        bool twinSynced = IsLosslessTwinSynced(out string twinLabel);
        if (studioOptTwinHintText != null) {
            if (twinSynced) {
                studioOptTwinHintText.Text = string.Format(S["StudioContextHybridReadyClean"], twinLabel);
                studioOptTwinHintText.SetTextColor(OneUiTheme.IconSystemInfo);
            } else if (opts.EnableLosslessTwinRestoration) {
                studioOptTwinHintText.Text = S["StudioW12ContextHybridNoTwin"];
                studioOptTwinHintText.SetTextColor(OneUiTheme.PeachText);
            } else {
                studioOptTwinHintText.Text = S["StudioOptTwinDesc"];
                studioOptTwinHintText.SetTextColor(OneUiTheme.TextSub);
            }
        }

        if (studioW12ContextText != null) {
            List<string> tags = [GetStudioBaseFormatLabel(opts.BaseFormat)];
            if (opts.EnableAiSpatialUpmix) {
                tags.Add(IsActiveTrackMultichannelOrAtmos() ? S["StudioTagAtmosAi"] : S["StudioTagAiUpmix"]);
            }
            if (opts.EnableKaraokeInstrumental) {
                tags.Add(string.Format(S["StudioTagKaraokeFmt"], vocPct));
            }
            if (opts.IncludeRecordedPerformance) {
                tags.Add(S["StudioTagVocalTake"]);
            }
            if (opts.EnableLosslessTwinRestoration) {
                tags.Add(S["StudioTagLosslessTwin"]);
            }
            studioW12ContextText.Text = string.Format(
                S["StudioExportRecipeSummaryFmt"],
                string.Join(" + ", tags),
                store.ResolveTruePeakCeilingDbtp());
            studioW12ContextText.SetTextColor(OneUiTheme.TextSecondary);
        }

        RefreshStudioRecorderUi();
    }

    bool IsComposableExportNeuralReady(StudioComposableExportOptions opts, out bool runtimeInstalled) {
        runtimeInstalled = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
        if (opts == null) {
            return runtimeInstalled;
        }
        bool needsNeural = (opts.EnableAiSpatialUpmix && !IsActiveTrackMultichannelOrAtmos()) ||
                           opts.EnableKaraokeInstrumental ||
                           opts.EnableLosslessTwinRestoration;
        if (!needsNeural) {
            return true;
        }
        if (!runtimeInstalled || neuralOrchestrator == null) {
            return false;
        }
        NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        if (opts.EnableAiSpatialUpmix && !IsActiveTrackMultichannelOrAtmos()) {
            return neuralOrchestrator.BuildPlan(NeuralPipelineGoal.StemSeparation, cfg).IsRunnable;
        }
        if (opts.EnableKaraokeInstrumental) {
            return neuralOrchestrator.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, cfg).IsRunnable ||
                   neuralOrchestrator.BuildPlan(NeuralPipelineGoal.StemSeparation, cfg).IsRunnable;
        }
        return true;
    }

    StudioBatchExportJob BuildComposableExportJob(StudioComposableExportOptions opts, string dir, string baseName) {
        string ext = StudioBatchExporter.GetDefaultExtension(opts.BaseFormat);
        List<string> slugParts = [opts.BaseFormat.ToString()];
        if (opts.EnableAiSpatialUpmix) slugParts.Add("AI");
        if (opts.EnableKaraokeInstrumental) slugParts.Add("Karaoke");
        if (opts.IncludeRecordedPerformance) slugParts.Add("VocalTake");
        if (opts.EnableLosslessTwinRestoration) slugParts.Add("Twin");
        string suffix = string.Join("_", slugParts);
        string outPath = Path.Combine(dir, $"{baseName}_{suffix}{ext}");
        int n = 1;
        while (File.Exists(outPath)) {
            outPath = Path.Combine(dir, $"{baseName}_{suffix}_{n++}{ext}");
        }

        PerformanceRecordingTake take = playerEngine?.LiveMicBridge?.LatestRecordedTake;
        return new StudioBatchExportJob {
            TrackName = baseName,
            Preset = opts.BaseFormat switch {
                StudioBaseOutputFormat.EclipsaIamf => StudioExportPreset.EclipsaAudioIamf,
                StudioBaseOutputFormat.Ambisonics360 => StudioExportPreset.AmbisonicsHoa3rdOrder,
                StudioBaseOutputFormat.DolbyAtmosAdm => StudioExportPreset.AdmBwfItuBs2076,
                StudioBaseOutputFormat.Cinema714 => StudioExportPreset.HybridNeuralDirAc714,
                _ => StudioExportPreset.PersonalizedLosslessBinaural
            },
            ComposableOptions = opts,
            OutputPath = outPath,
            BitDepth = store.Force24Bit ? BitDepth.Int24 : BitDepth.Int16,
            TruePeakCeilingDbtp = store.ResolveTruePeakCeilingDbtp(),
            KaraokeVocalLevel = Math.Clamp(store.KaraokeVocalLevel, 0f, 1f),
            PreserveBackingVocals = store.KaraokePreserveBackingVocals,
            VocalDnaProfile = playerEngine?.VocalMixMatchStage?.ActiveDna,
            LiveMicMonoCapture = opts.IncludeRecordedPerformance ? take?.MonoPcm : null,
            LosslessTwinProvider = activeLosslessTwin ?? playerEngine?.SceneHub?.ActiveLosslessReference,
            NeuralRuntime = neuralRuntime,
            NeuralOrchestrator = neuralOrchestrator,
            NeuralConfig = store.BuildNeuralAudioConfig(),
            LgsrConfig = store.BuildLgsrConfig(),
            PostProcessingStages = playerEngine?.SceneHub?.PostProcessingStages,
            OnSeparatedTrackReady = separated => {
                if (separated != null) {
                    playerEngine?.BindSeparatedNeuralTrack(separated);
                    RunOnUiThread(() => {
                        SyncNeuralRuntimeDiagnostics();
                        RefreshNeuralAudioUi();
                        RefreshNeuralPrepUi();
                    });
                }
            },
            HoaOrder = 3
        };
    }

    void StartStudioComposableExport() {
        if (isOperationRunning || isPreparingTrueHD) {
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
            pendingPostTrueHDStudioAction = StartStudioComposableExport;
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            return;
        }

        // Stop any active recording cleanly before exporting so the recorded take is finalized
        if (playerEngine?.LiveMicBridge?.IsRecordingPerformance == true) {
            StopStudioPerformanceRecording(autoSelectInExport: true);
        }

        StudioComposableExportOptions opts = BuildCurrentComposableOptions();
        if (opts.IncludeRecordedPerformance && !(playerEngine?.LiveMicBridge?.HasRecordedPerformance ?? false)) {
            ShowError(S["StudioRecNeedTakeToast"]);
            return;
        }
        if (opts.EnableAiSpatialUpmix && !IsActiveTrackMultichannelOrAtmos() &&
            !IsComposableExportNeuralReady(opts, out bool runtimeInstalled)) {
            ShowError(runtimeInstalled ? S["NeuralModelNeedInstall"] : S["NeuralRuntimeNeedInstall"]);
            return;
        }
        if (opts.EnableLosslessTwinRestoration && !IsLosslessTwinSynced(out _)) {
            ShowError(S["LgsrMasterNeedsTwin"]);
            return;
        }

        playerEngine?.SetPerformancePreviewTake(null, enabled: false);
        StopPlayback();
        string dir = AndroidFileHelper.GetWorkingOutputDir(this, "Renderizados");
        string baseName = Path.GetFileNameWithoutExtension(LoadedFile?.Path ?? "master");
        StudioBatchExportJob job = BuildComposableExportJob(opts, dir, baseName);
        string label = GetStudioBaseFormatLabel(opts.BaseFormat);

        RunBackgroundOperation(token => {
            RunOnUiThread(() => SetConvertStatus(S["Starting"], 0));
            StudioBatchExportResult res = StudioBatchExporter.ExportSingle(
                active,
                job,
                p => RunOnUiThread(() => SetConvertStatus(label, p)),
                token);
            if (!res.Succeeded) {
                throw new InvalidOperationException(res.ErrorMessage);
            }
            RunOnUiThread(() => SetConvertStatus(
                string.Format(S["StudioW12DoneFormat"], Path.GetFileName(res.OutputPath), res.IntegratedLufs, res.MaxTruePeakDbtp),
                1.0));
        }, job.OutputPath);
    }

    void SetStudioExportControlsBusy(bool running) {
        if (studioBaseFormatSelector != null) {
            studioBaseFormatSelector.Enabled = !running;
            studioBaseFormatSelector.Alpha = running ? 0.55f : 1f;
        }
        if (studioOptAiUpmixSwitch != null) studioOptAiUpmixSwitch.Enabled = !running;
        if (studioOptKaraokeSwitch != null) studioOptKaraokeSwitch.Enabled = !running;
        if (studioOptPerformanceSwitch != null) studioOptPerformanceSwitch.Enabled = !running;
        if (studioOptLosslessTwinSwitch != null) studioOptLosslessTwinSwitch.Enabled = !running;
        if (studioOptHeadphonePersonalSwitch != null) studioOptHeadphonePersonalSwitch.Enabled = !running;
        if (studioRecRecordBtn != null) studioRecRecordBtn.Enabled = !running;
        if (studioRecPreviewBtn != null) studioRecPreviewBtn.Enabled = !running;
        if (studioRecDiscardBtn != null) studioRecDiscardBtn.Enabled = !running;
    }

    static string FormatMmSs(double seconds) {
        int totalSec = Math.Max(0, (int)Math.Floor(seconds));
        return $"{totalSec / 60:00}:{totalSec % 60:00}";
    }
}
