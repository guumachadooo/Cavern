using Android;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.External;
using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

using CavernizeAndroid.Audio;
using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using VoidX.WPF.FFmpeg;

using Path = System.IO.Path;

namespace CavernizeAndroid;


public partial class MainActivity {
    readonly Dictionary<string, (double OffsetSamples, double DriftPpm, int SampleRate, float Confidence)> crossVariantOffsetByCanonicalSong = new(StringComparer.OrdinalIgnoreCase);

    (double OffsetSamples, double DriftPpm, bool HasAlignment) ComputeCrossVariantNeuralAlignment(CavernizeTrack targetTrack, string targetPath) {
        if (targetTrack == null || playerEngine?.NeuralStage == null) {
            return (0.0, 0.0, false);
        }
        Cavernize.Logic.Spatial.NeuralSeparatedTrack activeSep =
            playerEngine.NeuralStage.ActiveKaraokeTrack ?? playerEngine.NeuralStage.ActiveSpatialUpmixTrack;
        if (activeSep == null) {
            return (0.0, 0.0, false);
        }
        bool targetIsAtmos = IsTrackMultichannelOrAtmos(targetTrack, targetPath);
        bool stemsOnAtmosTimeline = activeSep.DerivedFromLosslessTwin ||
            IsPathAtmosOrSpatialContainer(activeSep.SourceTrackKey);
        if (targetIsAtmos == stemsOnAtmosTimeline) {
            return (0.0, 0.0, true);
        }

        string canon = NeuralTrackCacheService.ExtractCanonicalSongStem(
            !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : targetPath);
        double baseOffset = 0.0;
        double baseDriftPpm = 0.0;
        int offsetSr = 48000;
        if (activeLosslessTwin != null && activeLosslessTwin.HasSyncedLosslessTwin) {
            baseOffset = activeLosslessTwin.SyncResult.BaseOffsetSamples;
            baseDriftPpm = activeLosslessTwin.SyncResult.DriftPpm;
            offsetSr = activeLosslessTwin.SampleRate >= 8000 ? activeLosslessTwin.SampleRate : 48000;
        } else if (!string.IsNullOrEmpty(canon) && crossVariantOffsetByCanonicalSong.TryGetValue(canon, out var cached)) {
            baseOffset = cached.OffsetSamples;
            baseDriftPpm = cached.DriftPpm;
            offsetSr = cached.SampleRate >= 8000 ? cached.SampleRate : 48000;
        } else {
            return (0.0, 0.0, false);
        }

        int outSr = playerEngine.OutputSampleRate >= 8000 ? playerEngine.OutputSampleRate : 48000;
        double scaledOffset = offsetSr == outSr ? baseOffset : baseOffset * (outSr / (double)offsetSr);
        // SubSampleSyncEngine: losslessIndex = BaseOffsetSamples + atmosIndex * (1 + DriftPpm*1e-6)
        // Atmos stems playing on FLAC -> stemIdx = losslessIdx * (1 - DriftPpm*1e-6) - BaseOffsetSamples
        // FLAC stems playing on Atmos -> stemIdx = atmosIdx * (1 + DriftPpm*1e-6) + BaseOffsetSamples
        bool invert = stemsOnAtmosTimeline && !targetIsAtmos;
        return (invert ? -scaledOffset : scaledOffset, invert ? -baseDriftPpm : baseDriftPpm, true);
    }

    double ComputeCrossVariantNeuralOffsetSamples(CavernizeTrack targetTrack, string targetPath) =>
        ComputeCrossVariantNeuralAlignment(targetTrack, targetPath).OffsetSamples;

    double ComputeCrossVariantNeuralOffsetSamples(CavernizeTrack targetTrack, string targetPath, out double driftPpm) {
        var align = ComputeCrossVariantNeuralAlignment(targetTrack, targetPath);
        driftPpm = align.DriftPpm;
        return align.OffsetSamples;
    }

    void SyncNeuralReferenceOffsetForActiveTrack(CavernizeTrack activeTrack) {
        if (activeTrack == null || playerEngine?.NeuralStage == null) {
            return;
        }
        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        var align = ComputeCrossVariantNeuralAlignment(activeTrack, activePath);
        playerEngine.NeuralStage.ReferenceSampleOffset = align.OffsetSamples;
        playerEngine.NeuralStage.ReferenceDriftPpm = align.DriftPpm;
    }

    static bool IsTrackMultichannelOrAtmos(CavernizeTrack track, string path) {
        if (track != null) {
            if ((track.Renderer != null && (track.Renderer.HasObjects || track.Renderer.Objects.Count > 2)) ||
                (track.Reader != null && track.Reader.ChannelCount > 2) ||
                track.Codec == Codec.EnhancedAC3 ||
                track.Codec == Codec.TrueHD ||
                track.SourceCodec == Codec.TrueHD) {
                return true;
            }
        }
        return IsPathAtmosOrSpatialContainer(path);
    }

    static bool IsPathAtmosOrSpatialContainer(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return false;
        }
        string lower = path.ToLowerInvariant();
        return lower.EndsWith(".atmos", StringComparison.Ordinal) ||
               lower.EndsWith(".ec3", StringComparison.Ordinal) ||
               lower.EndsWith(".eac3", StringComparison.Ordinal) ||
               lower.EndsWith(".thd", StringComparison.Ordinal) ||
               lower.EndsWith(".mlp", StringComparison.Ordinal) ||
               lower.EndsWith(".m4a.mp4", StringComparison.Ordinal) ||
               lower.Contains("atmos", StringComparison.Ordinal);
    }

    void StartLosslessTwinDiscoveryAsync(CavernizeTrack activeTrack, bool forceRescan = false, int startupDelayMs = 0) {
        if (uiDestroyed || activeTrack == null || playerEngine == null) {
            return;
        }
        string activePath = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        Cavernize.Logic.Spatial.LgsrConfig lgsrCfg = store.BuildLgsrConfig();
        int cfgHash = HashCode.Combine(
            lgsrCfg.Enabled,
            lgsrCfg.AutoDiscoverTwin,
            lgsrCfg.BypassOnAlreadyLossless,
            lgsrCfg.RejectFakeLosslessTwins,
            lgsrCfg.FusionStrength,
            lgsrCfg.DynamicEnvelopeLock,
            lgsrCfg.ClippingGuardEnabled,
            HashCode.Combine(lgsrCfg.UseBitstreamCouplingSpxGuide, lgsrCfg.EnableTier3ResidualCleanup));

        if (!forceRescan &&
            lgsrCfg.Enabled &&
            activeLosslessTwin != null &&
            string.Equals(lastLgsrSyncedPath, activePath, StringComparison.OrdinalIgnoreCase) &&
            lastLgsrSyncedConfigHash == cfgHash) {
            return;
        }

        CancellationTokenSource prevTwinCts = losslessTwinCts;
        losslessTwinCts = null;
        try {
            prevTwinCts?.Cancel();
        } catch { }
        int gen = ++losslessTwinGeneration;

        bool alreadyLossless = LosslessTwinDiscoveryService.IsTrackAlreadyLossless(activeTrack, activePath);
        playerEngine.LgsrStage.SetActiveTrackLosslessStatus(alreadyLossless);

        // Detach stale lossless twin immediately while discovering the twin for the new track
        lastLgsrSyncedPath = "";
        Cavernize.Logic.Spatial.UniversalLosslessTwinProvider previous = activeLosslessTwin;
        activeLosslessTwin = null;
        playerEngine.BindLosslessReference(null);
        previous?.Dispose();

        playerEngine.ApplyLgsrConfig(lgsrCfg);
        RefreshLgsrUi();

        if (!lgsrCfg.Enabled || !lgsrCfg.AutoDiscoverTwin || (lgsrCfg.BypassOnAlreadyLossless && alreadyLossless)) {
            return;
        }

        CancellationTokenSource myTwinCts = new();
        losslessTwinCts = myTwinCts;
        CancellationToken ct = myTwinCts.Token;

        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        string title = currentMediaMeta?.Title ?? "";
        string artist = currentMediaMeta?.Artist ?? "";
        string album = currentMediaMeta?.Album ?? "";
        string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;

        _ = Task.Run(async () => {
            try {
                try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background); } catch { }
                if (startupDelayMs > 0) {
                    await Task.Delay(startupDelayMs, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                Cavernize.Logic.Spatial.UniversalLosslessTwinProvider twin =
                    await LosslessTwinDiscoveryService.DiscoverAndSyncAsync(
                        this,
                        snap,
                        activeTrack,
                        activePath,
                        title,
                        artist,
                        album,
                        lgsrCfg,
                        ffLoc,
                        ct).ConfigureAwait(false);

                RunOnUiThread(() => {
                    if (uiDestroyed || gen != losslessTwinGeneration) {
                        twin?.Dispose();
                        return;
                    }
                    activeLosslessTwin = twin;
                    lastLgsrSyncedPath = twin != null ? activePath : "";
                    lastLgsrSyncedConfigHash = cfgHash;
                    if (twin != null && twin.HasSyncedLosslessTwin) {
                        string canon = NeuralTrackCacheService.ExtractCanonicalSongStem(!string.IsNullOrWhiteSpace(title) ? title : activePath);
                        if (!string.IsNullOrEmpty(canon)) {
                            crossVariantOffsetByCanonicalSong[canon] = (
                                twin.SyncResult.BaseOffsetSamples,
                                twin.SyncResult.DriftPpm,
                                twin.SampleRate >= 8000 ? twin.SampleRate : 48000,
                                twin.SyncResult.Confidence);
                        }
                    }
                    playerEngine.BindLosslessReference(twin);
                    SyncNeuralReferenceOffsetForActiveTrack(activeTrack);
                    RefreshLgsrUi();
                });
            } catch (System.OperationCanceledException) {
                // Superseded by newer track selection
            } catch (ObjectDisposedException) when (ct.IsCancellationRequested || gen != losslessTwinGeneration) {
                // Superseded while previous CTS was tearing down
            } catch (Exception ex) {
                AppLogger.Warn($"W09 StartLosslessTwinDiscoveryAsync: {ex.Message}");
            } finally {
                try {
                    myTwinCts.Dispose();
                } catch { }
            }
        });
    }

    void CancelNeuralTrackPrep(bool silent = false) {
        neuralPrepGeneration++;
        CancellationTokenSource prevPrepCts = neuralPrepCts;
        neuralPrepCts = null;
        try {
            prevPrepCts?.Cancel();
        } catch { }
        isNeuralPrepRunning = false;
        activeNeuralPrepGoal = null;
        neuralPrepProgress = 0;
        neuralPrepStatusText = "";
        RefreshNeuralPrepUi();
        if (!silent) {
            Toast.MakeText(this, S["CancelledStatus"], ToastLength.Short).Show();
        }
    }

    bool IsActiveTrackMultichannelOrAtmos() {
        CavernizeTrack track = GetActiveTrack();
        if (track == null) {
            return false;
        }
        return (track.Renderer != null && (track.Renderer.HasObjects || track.Renderer.Objects.Count > 2)) ||
               (track.Reader != null && track.Reader.ChannelCount > 2) ||
               track.Codec == Codec.EnhancedAC3 ||
               track.Codec == Codec.TrueHD ||
               track.SourceCodec == Codec.TrueHD;
    }

    bool IsPlayTabAiActive() {
        if (store == null) {
            return false;
        }
        if (IsActiveTrackMultichannelOrAtmos()) {
            return store.PlayTabAtmosAiEnhancementEnabled;
        }
        return store.NeuralSpatialUpmixModeIndex != (int)Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.Off;
    }

    void TogglePlayTabAiProcessing() {
        if (uiDestroyed || store == null) {
            return;
        }
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || !activeTrack.Supported || activeTrack.Codec == Codec.TrueHD) {
            Toast.MakeText(this, S["NeuralPrepNoTrackToast"], ToastLength.Short).Show();
            return;
        }

        // Dolby Atmos / Multichannel path: Atmos-safe AI enhancement without ever collapsing dynamic objects
        if (IsActiveTrackMultichannelOrAtmos()) {
            bool nextOn = !store.PlayTabAtmosAiEnhancementEnabled;
            store.PlayTabAtmosAiEnhancementEnabled = nextOn;
            if (nextOn) {
                store.AtmosAwareUpmix = true;
                upmixingSettings.AtmosAware = true;
                _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
                if (!store.LgsrEnabled) {
                    store.LgsrEnabled = true;
                }
                if (playerEngine != null) {
                    playerEngine.LgsrBypassed = false;
                    playerEngine.ApplyLgsrConfig(store.BuildLgsrConfig());
                }
                StartLosslessTwinDiscoveryAsync(activeTrack);
                Toast.MakeText(this, S["PlayTabAiAtmosOnToast"], ToastLength.Short).Show();
            } else {
                if (playerEngine != null) {
                    playerEngine.LgsrBypassed = true;
                }
                Toast.MakeText(this, S["PlayTabAiOffToast"], ToastLength.Short).Show();
            }
            RefreshLgsrUi(); RefreshNeuralAudioUi(); RefreshNeuralPrepUi();
            RefreshSpatialChoreoClarifierUi(); RefreshBottomActionBarVisuals();
            return;
        }

        // Stereo / Mono path: Independent AI Spatial Upmix (never requires Karaoke)
        if (isNeuralPrepRunning && activeNeuralPrepGoal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation) {
            CancelNeuralTrackPrep(silent: false);
            return;
        }

        bool currentlyOn = store.NeuralSpatialUpmixModeIndex != (int)Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.Off;
        if (currentlyOn) {
            store.NeuralSpatialUpmixModeIndex = (int)Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.Off;
            _ = playerEngine?.ApplyNeuralConfigAsync(store.BuildNeuralAudioConfig());
            RefreshNeuralAudioUi(); RefreshNeuralPrepUi(); RefreshSpatialChoreoClarifierUi(); RefreshBottomActionBarVisuals();
            Toast.MakeText(this, S["PlayTabAiOffToast"], ToastLength.Short).Show();
            return;
        }

        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(
            Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, cfg);
        bool runtimeReady = neuralRuntime != null && neuralRuntime.IsAvailable;
        bool localReady = runtimeReady && plan != null && plan.IsRunnable;
        bool cloudReady = CanUseMvsepCloud(cfg);
        if (!localReady && !cloudReady) {
            ShowPlayTabAiModeDialog();
            Toast.MakeText(this, S["NeuralPrepNeedModelsToast"], ToastLength.Long).Show();
            return;
        }

        int targetMode = store.LastActiveNeuralSpatialUpmixModeIndex;
        store.NeuralSpatialUpmixModeIndex = targetMode;
        _ = playerEngine?.ApplyNeuralConfigAsync(store.BuildNeuralAudioConfig());
        RefreshNeuralAudioUi(); RefreshNeuralPrepUi(); RefreshSpatialChoreoClarifierUi(); RefreshBottomActionBarVisuals();

        if (!(playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems ?? false)) {
            PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation);
        } else {
            string modeName = targetMode == (int)Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.HybridNeuralDirAc
                ? S["PlayTabAiModeHybrid714"]
                : S["PlayTabAiModeStems3D"];
            Toast.MakeText(this, string.Format(S["PlayTabAiOnToastFmt"], modeName), ToastLength.Short).Show();
        }
    }

    void ShowPlayTabAiModeDialog() =>
        ShowUnifiedAiExtractionSheet(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation);

    void ShowKaraokeCacheInspectorDialog() =>
        ShowUnifiedAiExtractionSheet(Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke);

    /// <summary>
    /// W53.2 (Wave B): Context-aware unified Play &amp; Sing AI Extraction &amp; Multi-Variant Cache Sheet.
    /// Consolidates AI mode controls, Karaoke backing-vocal preference, incremental Stage-1 vocal reuse notice,
    /// multi-variant non-destructive cache switcher, and lossless storage compaction into one sheet.
    /// </summary>
    void ShowUnifiedAiExtractionSheet(Cavernize.Logic.Spatial.NeuralPipelineGoal contextGoal) {
        if (uiDestroyed || store == null) {
            return;
        }
        bool isKaraokeContext = contextGoal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke;
        bool isAtmos = IsActiveTrackMultichannelOrAtmos();
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        if (isKaraokeContext) {
            Switch backingSwitch = AddSwitchRow(
                content,
                S["KaraokePreserveBackingTitle"],
                store.KaraokePreserveBackingVocals,
                preserveBacking => {
                    store.KaraokePreserveBackingVocals = preserveBacking;
                    ApplyAndRefreshNeuralAudio();
                    NotifyUiDomainChanged(UiStateDomain.Playback | UiStateDomain.StudioExport, immediate: true);
                });
            backingSwitch.Tag = "karaokeBackingSwitch";
        } else if (isAtmos) {
            TextView desc = Label(S["PlayTabAiAtmosDialogDesc"], 12.6f, OneUiTheme.TextSecondary);
            desc.SetPadding(0, 0, 0, Dp(10));
            content.AddView(desc);

            Switch atmosSwitch = AddSwitchRow(
                content,
                S["PlayTabAiAtmosEnhanceTitle"],
                store.PlayTabAtmosAiEnhancementEnabled,
                on => {
                    store.PlayTabAtmosAiEnhancementEnabled = on;
                    if (on) {
                        store.AtmosAwareUpmix = true;
                        upmixingSettings.AtmosAware = true;
                        _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
                        store.LgsrEnabled = true;
                        if (playerEngine != null) {
                            playerEngine.LgsrBypassed = false;
                            playerEngine.ApplyLgsrConfig(store.BuildLgsrConfig());
                        }
                        if (GetActiveTrack() != null) {
                            StartLosslessTwinDiscoveryAsync(GetActiveTrack());
                        }
                    } else if (playerEngine != null) {
                        playerEngine.LgsrBypassed = true;
                    }
                    NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Upmix | UiStateDomain.Playback, immediate: true);
                },
                helpBody: S["TipSlot2CLgsr"]);
            atmosSwitch.Tag = "playTabAtmosSwitch";
        } else {
            TextView desc = Label(S["PlayTabAiStereoDialogDesc"], 12.4f, OneUiTheme.TextSecondary);
            desc.SetPadding(0, 0, 0, Dp(8));
            content.AddView(desc);

            OneUiInlineSelector modeSel = new(this, S["PlayTabAiModeTitle"]);
            modeSel.SetItems(new[] {
                S["PlayTabAiModeOff"],
                S["PlayTabAiModeStems3D"],
                S["PlayTabAiModeHybrid714"]
            }, Math.Clamp(store.NeuralSpatialUpmixModeIndex, 0, 2));
            modeSel.SelectionChanged += idx => SetNeuralSpatialUpmixMode(idx);
            content.AddView(WrapControlWithHelp(modeSel, S["PlayTabAiModeTitle"], S["TipNeuralUpmix"], bottomMarginDp: 6));

            Button pickerBtn = null;
            pickerBtn = PillButton(string.Format(S["AiPickerActiveModelFmt"], BuildActiveAiPickerSummaryText()), () =>
                ShowUnifiedAiModelPickerDialog(() => {
                    if (pickerBtn != null) pickerBtn.Text = string.Format(S["AiPickerActiveModelFmt"], BuildActiveAiPickerSummaryText());
                }));
            pickerBtn.TextSize = 11.4f;
            pickerBtn.SetSingleLine(true); pickerBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            content.AddView(pickerBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });
        }

        Android.App.Dialog activeDialog = null;
        AddSongAiCacheInspectorSection(
            content,
            contextGoal,
            () => activeDialog?.Dismiss());

        activeDialog = OneUiModalDialog.Show(
            this,
            isKaraokeContext ? S["KaraokeCacheDialogTitle"] : S["PlayTabAiDialogTitle"],
            null,
            customContent: content,
            icon: isKaraokeContext ? OneUiIconType.Microphone : OneUiIconType.Sparkles,
            iconBadgeColor: isKaraokeContext ? OneUiTheme.Primary : OneUiTheme.IconSliderMedia,
            positiveText: S["OpenAiSettingsBtn"],
            onPositive: () => JumpToSettingsCategory(2),
            negativeText: S["CloseBtn"],
            onHelp: ShowPlayTabSpatialHierarchyHelpDialog,
            helpContentDesc: S["PlayTabSpatialGuideTitle"]);
    }

    void TryAutoBindCachedNeuralStemsAsync(CavernizeTrack activeTrack, int startupDelayMs = 0) {
        if (uiDestroyed || activeTrack == null || neuralOrchestrator == null || neuralTrackCache == null || playerEngine == null) {
            return;
        }
        string sourceKey = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        if (string.IsNullOrEmpty(sourceKey) || !File.Exists(sourceKey)) {
            return;
        }
        int gen = neuralPrepGeneration;
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        int sr = activeTrack.SampleRate >= 8000 ? activeTrack.SampleRate : 48000;

        _ = Task.Run(async () => {
            try {
                try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background); } catch { }
                if (startupDelayMs > 0) {
                    await Task.Delay(startupDelayMs).ConfigureAwait(false);
                }
                if (uiDestroyed || gen != neuralPrepGeneration) {
                    return;
                }
                FileInfo fi = new(sourceKey);
                Cavernize.Logic.Spatial.NeuralSeparatedTrack spatialUpmixTrack = null;
                Cavernize.Logic.Spatial.NeuralSeparatedTrack karaokeTrack = null;
                Cavernize.Logic.Spatial.NeuralSeparatedTrack dnaTrack = null;

                string summaryTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                    ? currentMediaMeta.Title
                    : Path.GetFileNameWithoutExtension(sourceKey);
                string summaryArtist = (currentMediaMeta?.Artist ?? "").Trim();

                // W48 + BP W3: Cache-first auto-bind for BOTH Spatial Upmix AND Karaoke caches across backend/preset changes
                Cavernize.Logic.Spatial.NeuralExecutionPlan spatialPlan = neuralOrchestrator.BuildPlan(
                    Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, cfg);
                if (CanUseMvsepCloud(cfg)) {
                    Cavernize.Logic.Spatial.MvsepAlgorithmPreset cloudSpatialPreset =
                        ResolveMvsepPresetForGoal(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, cfg);
                    string cloudSpatialKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, BuildMvsepPlanSignature(cloudSpatialPreset, cfg));
                    if (neuralTrackCache.TryLoad(cloudSpatialKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedCloudSpatial)) {
                        spatialUpmixTrack = cachedCloudSpatial;
                    }
                }
                if (spatialUpmixTrack == null && spatialPlan.IsRunnable) {
                    string cacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, spatialPlan.PlanSignature);
                    if (neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedSpatial)) {
                        spatialUpmixTrack = cachedSpatial;
                    }
                }
                if (spatialUpmixTrack == null &&
                    neuralTrackCache.TryLoadBestForSourceTrack(
                        sourceKey, summaryTitle, summaryArtist,
                        Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation,
                        out Cavernize.Logic.Spatial.NeuralSeparatedTrack fallbackSpatial)) {
                    spatialUpmixTrack = fallbackSpatial;
                }

                Cavernize.Logic.Spatial.NeuralExecutionPlan karaokePlan = neuralOrchestrator.BuildPlan(
                    Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke, cfg);
                if (CanUseMvsepCloud(cfg)) {
                    Cavernize.Logic.Spatial.MvsepAlgorithmPreset cloudKaraPreset =
                        ResolveMvsepPresetForGoal(Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke, cfg);
                    string cloudKaraKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, BuildMvsepPlanSignature(cloudKaraPreset, cfg));
                    if (neuralTrackCache.TryLoad(cloudKaraKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedCloudKara)) {
                        karaokeTrack = cachedCloudKara;
                    }
                }
                if (karaokeTrack == null && karaokePlan.IsRunnable) {
                    string cacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, karaokePlan.PlanSignature);
                    if (neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedKaraoke)) {
                        karaokeTrack = cachedKaraoke;
                    }
                }
                if (karaokeTrack == null &&
                    neuralTrackCache.TryLoadBestForSourceTrack(
                        sourceKey, summaryTitle, summaryArtist,
                        Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke,
                        out Cavernize.Logic.Spatial.NeuralSeparatedTrack fallbackKaraoke)) {
                    karaokeTrack = fallbackKaraoke;
                }

                // Also check dedicated Slot 7A VocalMixDna cache (includes Dereverb dry/wet analysis when available)
                Cavernize.Logic.Spatial.NeuralExecutionPlan dnaPlan = neuralOrchestrator.BuildPlan(
                    Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna, cfg);
                if (dnaPlan.IsRunnable) {
                    string dnaCacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, dnaPlan.PlanSignature);
                    if (neuralTrackCache.TryLoad(dnaCacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedDnaTrack)) {
                        dnaTrack = cachedDnaTrack;
                    }
                }

                // B3: GCC-PHAT cross-variant alignment for cached FLAC <-> Atmos stems on cold start or variant load.
                // Never applies an unaligned cross-variant karaoke stem at offset 0; rejects incompatible studio masters cleanly.
                Cavernize.Logic.Spatial.NeuralSeparatedTrack crossRef = karaokeTrack ?? spatialUpmixTrack;
                if (crossRef != null &&
                    !EnsureCrossVariantAlignmentForCachedTrack(activeTrack, sourceKey, summaryTitle, sr, ffmpeg?.Found == true ? ffmpeg.Location : null, crossRef, CancellationToken.None)) {
                    karaokeTrack = null;
                }

                Cavernize.Logic.Spatial.NeuralSeparatedTrack dnaSource = dnaTrack ?? karaokeTrack ?? spatialUpmixTrack;
                Cavern.VocalChain.VocalMixDnaProfile extractedDna = dnaSource != null
                    ? Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(dnaSource, summaryTitle)
                    : null;
                if (extractedDna != null && extractedDna.IsExtractedFromSong) {
                    SaveCachedSongVocalDna(activeTrack, extractedDna);
                }

                if (spatialUpmixTrack != null || karaokeTrack != null || extractedDna != null) {
                    if (spatialUpmixTrack != null && !uiDestroyed && gen == neuralPrepGeneration && GetActiveTrack() == activeTrack) await playerEngine.BindSpatialUpmixTrackAsync(spatialUpmixTrack).ConfigureAwait(false);
                    if (karaokeTrack != null && !uiDestroyed && gen == neuralPrepGeneration && GetActiveTrack() == activeTrack) playerEngine.BindKaraokeReferenceTrack(karaokeTrack);
                    RunOnUiThread(() => {
                        if (uiDestroyed || gen != neuralPrepGeneration || GetActiveTrack() != activeTrack) return;
                        SyncNeuralReferenceOffsetForActiveTrack(activeTrack);
                        ApplyExtractedSongDnaOnUiThread(extractedDna);
                        NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Playback | UiStateDomain.Upmix, immediate: true);
                    });
                }
            } catch (Exception ex) {
                AppLogger.Warn($"W18/W19.b/W48 TryAutoBindCachedNeuralStemsAsync: {ex.Message}");
            }
        });
    }

    bool EnsureCrossVariantAlignmentForCachedTrack(
        CavernizeTrack activeTrack,
        string sourceKey,
        string summaryTitle,
        int sr,
        string ffLoc,
        Cavernize.Logic.Spatial.NeuralSeparatedTrack crossRef,
        CancellationToken ct) {
        if (activeTrack == null || crossRef == null || string.IsNullOrWhiteSpace(sourceKey)) {
            return true;
        }
        bool targetIsAtmos = IsTrackMultichannelOrAtmos(activeTrack, sourceKey);
        bool stemsOnAtmosTimeline = crossRef.DerivedFromLosslessTwin ||
            IsPathAtmosOrSpatialContainer(crossRef.SourceTrackKey);
        if (targetIsAtmos == stemsOnAtmosTimeline) {
            return true;
        }
        string canonSong = NeuralTrackCacheService.ExtractCanonicalSongStem(
            !string.IsNullOrWhiteSpace(summaryTitle) ? summaryTitle : sourceKey);
        bool alreadyKnown = (activeLosslessTwin != null && activeLosslessTwin.HasSyncedLosslessTwin) ||
            (!string.IsNullOrEmpty(canonSong) && crossVariantOffsetByCanonicalSong.ContainsKey(canonSong));
        if (alreadyKnown) {
            return true;
        }
        if (LosslessTwinDiscoveryService.TryDecodeTrackToStereoFloat(
            sourceKey, sr, ffLoc, ct, out float[] playL, out float[] playR, out int playSr) &&
            playL != null && playR != null && playL.Length >= 4096) {
            int mLen = Math.Min(playL.Length, playR.Length);
            float[] playMono = new float[mLen];
            for (int i = 0; i < mLen; i++) {
                playMono[i] = 0.5f * (playL[i] + playR[i]);
            }
            Cavernize.Logic.Spatial.CrossVariantAlignmentResult cvAlign =
                Cavernize.Logic.Spatial.CrossVariantVocalAligner.Align(playMono, playSr, crossRef);
            if (cvAlign.IsAligned) {
                bool invertToCanonical = stemsOnAtmosTimeline && !targetIsAtmos;
                double canonOffset = invertToCanonical ? -cvAlign.OffsetSamples : cvAlign.OffsetSamples;
                double canonDrift = invertToCanonical ? -cvAlign.DriftPpm : cvAlign.DriftPpm;
                if (!string.IsNullOrEmpty(canonSong)) {
                    crossVariantOffsetByCanonicalSong[canonSong] = (canonOffset, canonDrift, playSr, cvAlign.Confidence);
                }
                AppLogger.Info($"B3 CrossVariantVocalAligner locked '{canonSong}' via {cvAlign.ProbeSource} (offset={cvAlign.OffsetSamples:0.00} smp, drift={cvAlign.DriftPpm:0.0} ppm, conf={cvAlign.Confidence:0.00}).");
                return true;
            }
            if (cvAlign.ShouldPreferSeparateExtraction) {
                AppLogger.Info($"B3 CrossVariantVocalAligner rejected mismatched cross-variant cache for '{canonSong}' (conf={cvAlign.Confidence:0.00}, probe={cvAlign.ProbeSource}); falling back to separate extraction.");
                return false;
            }
        }
        return true;
    }

    bool CanUseMvsepCloud(Cavernize.Logic.Spatial.NeuralAudioConfig cfg) {
        if (store == null || cfg == null) {
            return false;
        }
        return (cfg.ExecutionBackend == Cavernize.Logic.Spatial.NeuralExecutionBackend.MvsepCloud ||
                cfg.ExecutionBackend == Cavernize.Logic.Spatial.NeuralExecutionBackend.AutoHybrid) &&
               !string.IsNullOrWhiteSpace(store.MvsepApiToken);
    }

    Cavernize.Logic.Spatial.MvsepAlgorithmPreset ResolveMvsepPresetForGoal(
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal,
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg) {
        if (!string.IsNullOrWhiteSpace(store?.MvsepPresetId)) {
            return Cavernize.Logic.Spatial.MvsepAlgorithmPreset.ResolveForGoal(
                goal,
                cfg?.KaraokePreserveBackingVocals ?? true,
                store.MvsepPresetId);
        }
        if (cfg != null && cfg.StudioBundlePreset == Cavernize.Logic.Spatial.NeuralEnsembleBundlePreset.StudioFullBand6Stem) {
            return Cavernize.Logic.Spatial.MvsepAlgorithmPreset.FindOrDefault("mvsep_bs_roformer_6stem");
        }
        return Cavernize.Logic.Spatial.MvsepAlgorithmPreset.ResolveForGoal(
            goal,
            cfg?.KaraokePreserveBackingVocals ?? true,
            store?.MvsepPresetId);
    }

    static string BuildMvsepPlanSignature(
        Cavernize.Logic.Spatial.MvsepAlgorithmPreset preset,
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg,
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal = Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation) =>
        Cavernize.Logic.Spatial.NeuralCacheConfigDiff.BuildMvsepPlanSignature(preset, cfg, goal);

    void TogglePureInstrumentalMode(bool fromSingDock = false) {
        if (uiDestroyed || store == null) {
            return;
        }
        bool nextPreserveBacking = !store.KaraokePreserveBackingVocals;
        store.KaraokePreserveBackingVocals = nextPreserveBacking;
        if (!nextPreserveBacking && !store.SpatialKaraokeEnabled && fromSingDock) {
            // Turning on Pure Instrumental from Sing Dock smoothly engages karaoke attenuation
            store.SpatialKaraokeEnabled = true;
            if (store.KaraokeVocalLevel >= 0.99f) {
                store.KaraokeVocalLevel = 0.0f;
            }
        }
        // Apply instantly via zero-alloc DSP update (NEVER auto-spawn a heavy 2.3 GB ONNX separation pass on toggle,
        // which caused overlapping native allocations and Android LMKD LOW_MEMORY kills). Users can explicitly
        // re-extract with updated settings via the Play / Karaoke Cache Status & Diff panel.
        ApplyAndRefreshNeuralAudio();
        RefreshLyricsSingDockUi();
        RefreshStudioW12ContextUi();

        string toastText = nextPreserveBacking
            ? S["KaraokeBackingKeptToast"]
            : S["KaraokePureInstrumentalToast"];
        Toast.MakeText(this, toastText, ToastLength.Short).Show();
    }

    void PrepareActiveTrackNeuralStemsAsync(
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal,
        bool silentIfNotReady = false,
        bool forceReextract = false) {
        if (uiDestroyed) {
            return;
        }
        if (isNeuralPrepRunning) {
            CancelNeuralTrackPrep(silent: false);
            return;
        }
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || !activeTrack.Supported || activeTrack.Codec == Codec.TrueHD) {
            if (!silentIfNotReady) {
                Toast.MakeText(this, S["NeuralPrepNoTrackToast"], ToastLength.Short).Show();
            }
            return;
        }
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(goal, cfg);
        bool runtimeReady = neuralRuntime != null && neuralRuntime.IsAvailable;
        bool localReady = runtimeReady && plan != null && plan.IsRunnable;
        bool cloudRequested = CanUseMvsepCloud(cfg) && goal != Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna;
        if (!localReady && !cloudRequested) {
            if (!silentIfNotReady) {
                string msgKey = goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna
                    ? "VocalDnaNeedModelsToast"
                    : "NeuralPrepNeedModelsToast";
                Toast.MakeText(this, S[msgKey], ToastLength.Long).Show();
            }
            return;
        }

        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        string decodePath = activePath;
        bool derivedFromTwin = false;
        if (cfg.UseLosslessTwinForKaraoke &&
            activeLosslessTwin != null &&
            activeLosslessTwin.HasSyncedLosslessTwin &&
            !string.IsNullOrEmpty(activeLosslessTwin.Metadata?.FilePath) &&
            File.Exists(activeLosslessTwin.Metadata.FilePath)) {
            decodePath = activeLosslessTwin.Metadata.FilePath;
            derivedFromTwin = true;
        }
        if (string.IsNullOrEmpty(decodePath) || !File.Exists(decodePath)) {
            if (!silentIfNotReady) {
                Toast.MakeText(this, S["NeuralPrepNoTrackToast"], ToastLength.Short).Show();
            }
            return;
        }

        CancellationTokenSource prevPrepCts = neuralPrepCts;
        CancellationTokenSource myPrepCts = new();
        neuralPrepCts = myPrepCts;
        try {
            prevPrepCts?.Cancel();
        } catch { }
        CancellationToken ct = myPrepCts.Token;
        int myGen = ++neuralPrepGeneration;
        isNeuralPrepRunning = true;
        activeNeuralPrepGoal = goal;
        neuralPrepProgress = 0.02;
        neuralPrepStatusText = S["NeuralPrepDecodingStatus"];
        RefreshNeuralPrepUi();

        string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
        int prefSr = activeTrack.SampleRate >= 8000 ? activeTrack.SampleRate : 48000;
        string summaryTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileNameWithoutExtension(activePath);
        string summaryArtist = (currentMediaMeta?.Artist ?? "").Trim();
        Cavernize.Logic.Spatial.MvsepAlgorithmPreset cloudPreset = cloudRequested
            ? ResolveMvsepPresetForGoal(goal, cfg)
            : null;
        Cavernize.Logic.Spatial.MvsepCloudOptions cloudOptions = cloudRequested
            ? store.BuildMvsepCloudOptions()
            : null;

        _ = Task.Run(async () => {
            try {
                ct.ThrowIfCancellationRequested();
                FileInfo keyInfo = new(activePath);

                // Fast path for VocalMixDna: if full separated stems for this track are already loaded in RAM or cached on disk,
                // extract Song Vocal DNA immediately (<0.3s) without re-running ONNX separation!
                if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna) {
                    Cavernize.Logic.Spatial.NeuralSeparatedTrack existingForDna = playerEngine?.NeuralStage?.ActiveSeparatedTrack;
                    if (existingForDna == null ||
                        existingForDna.Stems == null ||
                        existingForDna.Stems.Count == 0 ||
                        !string.Equals(existingForDna.SourceTrackKey, activePath, StringComparison.OrdinalIgnoreCase)) {
                        existingForDna = null;
                        if (neuralTrackCache != null) {
                            Cavernize.Logic.Spatial.NeuralPipelineGoal[] reuseGoals = [
                                Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation,
                                Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke
                            ];
                            for (int g = 0; g < reuseGoals.Length; g++) {
                                Cavernize.Logic.Spatial.NeuralExecutionPlan rp = neuralOrchestrator.BuildPlan(reuseGoals[g], cfg);
                                if (!rp.IsRunnable) {
                                    continue;
                                }
                                string rk = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                                    activePath, keyInfo.Length, keyInfo.LastWriteTimeUtc.Ticks, prefSr, rp.PlanSignature);
                                if (neuralTrackCache.TryLoad(rk, out Cavernize.Logic.Spatial.NeuralSeparatedTrack reusable, ct)) {
                                    existingForDna = reusable;
                                    break;
                                }
                            }
                        }
                    }

                    if (existingForDna != null) {
                        Cavern.VocalChain.VocalMixDnaProfile instantDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                            existingForDna, summaryTitle);
                        if (instantDna != null && instantDna.IsExtractedFromSong) {
                            SaveCachedSongVocalDna(activeTrack, instantDna);
                            RunOnUiThread(() => {
                                if (uiDestroyed || myGen != neuralPrepGeneration) {
                                    return;
                                }
                                isNeuralPrepRunning = false;
                                activeNeuralPrepGoal = null;
                                neuralPrepProgress = 1.0;
                                neuralPrepStatusText = "";
                                if (!(playerEngine.NeuralStage?.HasPlayableSeparatedStems ?? false)) {
                                    playerEngine.BindSeparatedNeuralTrack(existingForDna);
                                }
                                ApplyExtractedSongDnaOnUiThread(instantDna);
                                NotifyUiDomainChanged(UiStateDomain.Neural, immediate: true);
                                Toast.MakeText(this, string.Format(S["VocalDnaReadyToast"], instantDna.TargetVocalLufs, instantDna.ReverbRt60Sec, instantDna.ActiveWindowCount), ToastLength.Short).Show();
                            });
                            return;
                        }
                    }
                }

                string twinCacheTag = (derivedFromTwin && activeLosslessTwin != null && activeLosslessTwin.HasSyncedLosslessTwin)
                    ? $"|twin:{Math.Round(activeLosslessTwin.SyncResult.BaseOffsetSamples, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}:{Math.Round(activeLosslessTwin.SyncResult.DriftPpm, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    : "|native";
                string activePlanSig = cloudRequested
                    ? BuildMvsepPlanSignature(cloudPreset, cfg)
                    : (plan?.PlanSignature ?? "");
                string cacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                    activePath, keyInfo.Length, keyInfo.LastWriteTimeUtc.Ticks, prefSr, activePlanSig + twinCacheTag);
                string cloudPlainCacheKey = cloudRequested
                    ? Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        activePath, keyInfo.Length, keyInfo.LastWriteTimeUtc.Ticks, prefSr, activePlanSig)
                    : cacheKey;

                bool allowCache = neuralTrackCache != null && cfg.TrackCacheEnabled && neuralTrackCache.IsEnabled;
                // Instant Slot 7B cache-first hit before decoding PCM or uploading to MVSEP Cloud
                // (bypassed only when the user explicitly requests "Re-extract Stems" or cross-variant alignment rejects a mismatched master).
                if (allowCache && !forceReextract &&
                    (neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedTrack, ct) ||
                     (cloudRequested && neuralTrackCache.TryLoad(cloudPlainCacheKey, out cachedTrack, ct)) ||
                     neuralTrackCache.TryLoadBestForSourceTrack(activePath, summaryTitle, summaryArtist, goal, out cachedTrack, ct)) &&
                    (goal != Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke ||
                     EnsureCrossVariantAlignmentForCachedTrack(activeTrack, activePath, summaryTitle, prefSr, ffLoc, cachedTrack, ct))) {
                    Cavern.VocalChain.VocalMixDnaProfile cachedDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                        cachedTrack, summaryTitle);
                    if (cachedDna != null && cachedDna.IsExtractedFromSong) {
                        SaveCachedSongVocalDna(activeTrack, cachedDna);
                    }
                    if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation || goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                        await BindSpatialStemsOffUiThreadAsync(cachedTrack, goal, ct).ConfigureAwait(false);
                    }
                    RunOnUiThread(() => {
                        if (uiDestroyed || myGen != neuralPrepGeneration) {
                            return;
                        }
                        isNeuralPrepRunning = false;
                        activeNeuralPrepGoal = null;
                        neuralPrepProgress = 1.0;
                        neuralPrepStatusText = "";
                        SyncNeuralReferenceOffsetForActiveTrack(activeTrack);
                        ApplyExtractedSongDnaOnUiThread(cachedDna);
                        NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Upmix, immediate: true);
                        string msg = (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna && cachedDna != null && cachedDna.IsExtractedFromSong)
                            ? string.Format(S["VocalDnaReadyToast"], cachedDna.TargetVocalLufs, cachedDna.ReverbRt60Sec, cachedDna.ActiveWindowCount)
                            : string.Format(S["NeuralPrepCacheHitToast"], cachedTrack.Stems.Count);
                        Toast.MakeText(this, msg, ToastLength.Short).Show();
                    });
                    return;
                }

                ct.ThrowIfCancellationRequested();
                float[] mixL = null;
                float[] mixR = null;
                int decodedSr = prefSr;
                bool usedAlignedTwin = false;
                if (derivedFromTwin && activeLosslessTwin != null && activeLosslessTwin.HasSyncedLosslessTwin) {
                    int atmosLen = activeTrack.Length > 0 && activeTrack.Length <= int.MaxValue
                        ? (int)activeTrack.Length
                        : 0;
                    if (atmosLen > 0 && activeLosslessTwin.TryExtractFullAlignedStereo(atmosLen, out float[] alignedL, out float[] alignedR)) {
                        mixL = alignedL;
                        mixR = alignedR;
                        decodedSr = activeLosslessTwin.SampleRate >= 8000 ? activeLosslessTwin.SampleRate : prefSr;
                        usedAlignedTwin = true;
                        AppLogger.Info($"W47 Neural Karaoke using sub-sample aligned Lossless Twin ({atmosLen} samples @ {decodedSr} Hz, offset={activeLosslessTwin.SyncResult.BaseOffsetSamples:0.00} smp, drift={activeLosslessTwin.SyncResult.DriftPpm:0.0} ppm).");
                    }
                }
                if (!usedAlignedTwin) {
                    if (!LosslessTwinDiscoveryService.TryDecodeTrackToStereoFloat(
                        decodePath, prefSr, ffLoc, ct, out mixL, out mixR, out decodedSr)) {
                        throw new InvalidOperationException("Unable to decode track audio for neural separation.");
                    }
                    // W47: Compensate any FFmpeg vs Cavern AudioReader MDCT/elst priming offset on Pure Atmos tracks
                    double primingShift = LosslessTwinDiscoveryService.AlignDecodedStereoToCavernTimeline(
                        activePath, mixL, mixR, decodedSr, ct);
                    if (Math.Abs(primingShift) >= 0.5) {
                        AppLogger.Info($"W47 Pure Atmos neural priming aligned to Cavern AudioReader timeline (shift={primingShift:0.00} samples @ {decodedSr} Hz).");
                    }
                }

                ct.ThrowIfCancellationRequested();
                Cavernize.Logic.Spatial.NeuralSeparatedTrack separated = null;
                float rtf = 0f;
                bool fromCache = false;

                Action<double, string> reportProgress = (p, msg) => RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    neuralPrepProgress = Math.Clamp(p, 0.0, 1.0);
                    neuralPrepStatusText = msg ?? "";
                    RefreshNeuralPrepUi();
                });

                // W53.2 (Wave B): Incremental Stage-1 vocal reuse across both RAM & disk cache.
                // When Karaoke needs Stage-2 Lead/Backing split and this song already has separated vocals
                // (e.g., from Play 4/6-stem separation), skip Stage 1 completely in both MVSEP Cloud and Local ONNX.
                Cavernize.Logic.Spatial.NeuralSeparatedTrack reusableStage1 = null;
                if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke && !forceReextract) {
                    Cavernize.Logic.Spatial.NeuralSeparatedTrack liveSpatial = playerEngine?.NeuralStage?.ActiveSpatialUpmixTrack;
                    if (liveSpatial != null &&
                        string.Equals(liveSpatial.SourceTrackKey, activePath, StringComparison.OrdinalIgnoreCase) &&
                        liveSpatial.FindStem("vocals") != null) {
                        reusableStage1 = liveSpatial;
                        AppLogger.Info("W53.2 Incremental Stage-1 vocal reuse: using live RAM SpatialUpmix stems.");
                    } else if (allowCache &&
                        neuralTrackCache.TryFindBestReusableVocalTrack(
                            activePath, summaryTitle, summaryArtist, decodedSr,
                            out Cavernize.Logic.Spatial.NeuralSeparatedTrack diskReusable,
                            out Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo reusableInfo,
                            ct)) {
                        reusableStage1 = diskReusable;
                        AppLogger.Info($"W53.2 Incremental Stage-1 vocal reuse: loaded cached vocals from '{reusableInfo?.OriginTag}' ({reusableInfo?.StemCount} stems).");
                    }
                }

                if (cloudRequested) {
                    try {
                        System.Diagnostics.Stopwatch cloudSw = System.Diagnostics.Stopwatch.StartNew();
                        using Cavernize.Logic.Spatial.MvsepCloudClient mvsepClient = new();
                        if (cloudPreset.IsTwoStageCascade) {
                            string s1Key = $"{cloudPreset.Id}:s1";
                            string s2Key = $"{cloudPreset.Id}:s2";
                            string pendingS1Hash = null;
                            string pendingS2Hash = null;
                            if (store.TryGetPendingMvsepJobHash(cloudPlainCacheKey, s1Key, out string savedS1)) {
                                pendingS1Hash = savedS1;
                                AppLogger.Info($"MVSEP 2-Stage: Resuming Stage-1 job hash={savedS1} ({cloudPreset.Id}).");
                            }
                            if (store.TryGetPendingMvsepJobHash(cloudPlainCacheKey, s2Key, out string savedS2)) {
                                pendingS2Hash = savedS2;
                                AppLogger.Info($"MVSEP 2-Stage: Resuming Stage-2 vocals-only job hash={savedS2} ({cloudPreset.Id}).");
                            }
                            separated = await mvsepClient.SeparateTwoStageCascadeAsync(
                                sourceTrackKey: activePath,
                                mixLeft: mixL,
                                mixRight: mixR,
                                sampleRate: decodedSr,
                                stage1Preset: cloudPreset,
                                options: cloudOptions,
                                preExtractedStage1Track: reusableStage1,
                                displayTitle: summaryTitle,
                                artistName: summaryArtist,
                                externalDecoder: (rawBytes, fileName) => DecodeTempStemBytesWithFfmpeg(rawBytes, fileName, decodedSr, ffLoc, ct),
                                onProgress: reportProgress,
                                token: ct,
                                existingStage1JobHash: pendingS1Hash,
                                onStage1JobCreated: h => store.SavePendingMvsepJobHash(cloudPlainCacheKey, s1Key, h),
                                onStage1JobTerminal: _ => store.ClearPendingMvsepJobHash(cloudPlainCacheKey, s1Key),
                                existingStage2JobHash: pendingS2Hash,
                                onStage2JobCreated: h => store.SavePendingMvsepJobHash(cloudPlainCacheKey, s2Key, h),
                                onStage2JobTerminal: _ => store.ClearPendingMvsepJobHash(cloudPlainCacheKey, s2Key)).ConfigureAwait(false);
                        } else {
                            string pendingJobHash = null;
                            if (store.TryGetPendingMvsepJobHash(cloudPlainCacheKey, cloudPreset.Id, out string savedHash)) {
                                pendingJobHash = savedHash;
                                AppLogger.Info($"MVSEP Cloud: Resuming previously submitted job hash={savedHash} (preset={cloudPreset.Id}) to preserve API credits.");
                            }
                            separated = await mvsepClient.SeparateTrackAsync(
                                sourceTrackKey: activePath,
                                mixLeft: mixL,
                                mixRight: mixR,
                                sampleRate: decodedSr,
                                preset: cloudPreset,
                                options: cloudOptions,
                                displayTitle: summaryTitle,
                                artistName: summaryArtist,
                                externalDecoder: (rawBytes, fileName) => DecodeTempStemBytesWithFfmpeg(rawBytes, fileName, decodedSr, ffLoc, ct),
                                onProgress: reportProgress,
                                token: ct,
                                existingJobHash: pendingJobHash,
                                onJobCreated: createdHash => {
                                    store.SavePendingMvsepJobHash(cloudPlainCacheKey, cloudPreset.Id, createdHash);
                                    AppLogger.Info($"MVSEP Cloud: Saved active job hash={createdHash} for cancel-safe resume.");
                                },
                                onJobTerminal: _ => store.ClearPendingMvsepJobHash(cloudPlainCacheKey, cloudPreset.Id)).ConfigureAwait(false);
                        }
                        cloudSw.Stop();
                        double audioSec = decodedSr > 0 ? mixL.Length / (double)decodedSr : 1.0;
                        rtf = audioSec > 0.01 ? (float)(cloudSw.Elapsed.TotalSeconds / audioSec) : 0f;
                        if (separated != null && allowCache) {
                            neuralTrackCache.CompressionMode = cfg.TrackCacheCompressionMode;
                            neuralTrackCache.Save(cloudPlainCacheKey, separated, ct);
                            neuralTrackCache.EvictToLimit((long)cfg.TrackCacheMaxSizeMb * 1024L * 1024L);
                        }
                    } catch (System.OperationCanceledException) {
                        throw;
                    } catch (Exception cloudEx) when (cfg.ExecutionBackend == Cavernize.Logic.Spatial.NeuralExecutionBackend.AutoHybrid && localReady) {
                        AppLogger.Warn($"T3 MVSEP Cloud AutoHybrid fallback to On-Device ONNX: {Cavernize.Logic.Spatial.MvsepCloudClient.RedactSecrets(cloudEx.Message, cloudOptions?.ApiToken)}");
                        reportProgress(0.05, S["MvsepAutoFallbackStatus"]);
                        separated = null;
                    }
                }

                if (separated == null) {
                    if (!localReady) {
                        throw new InvalidOperationException("No local ONNX models available for fallback.");
                    }
                    Action<Cavernize.Logic.Spatial.NeuralSeparatedTrack> progressiveCallback =
                        goal != Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna
                            ? progressiveTrack => {
                                if (uiDestroyed || myGen != neuralPrepGeneration || progressiveTrack == null) return;
                                if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation) playerEngine.BindSpatialUpmixTrack(progressiveTrack);
                                else if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) playerEngine.BindKaraokeReferenceTrack(progressiveTrack);
                                RunOnUiThread(() => { if (!uiDestroyed && myGen == neuralPrepGeneration) { NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Upmix, immediate: true); } });
                            }
                            : null;

                    try {
                        (separated, rtf, fromCache) = neuralOrchestrator.ExecuteTrackPipeline(
                            sourceTrackKey: activePath,
                            fileSizeBytes: keyInfo.Length,
                            lastModifiedTicks: keyInfo.LastWriteTimeUtc.Ticks,
                            mixLeft: mixL,
                            mixRight: mixR,
                            sampleRate: decodedSr,
                            goal: goal,
                            config: cfg,
                            derivedFromLosslessTwin: usedAlignedTwin || derivedFromTwin,
                            onProgress: reportProgress,
                            token: ct,
                            onProgressiveTrackReady: progressiveCallback,
                            displayTitle: summaryTitle,
                            artistName: summaryArtist,
                            forceReextract: forceReextract,
                            preExtractedTrack: reusableStage1);
                    } catch (InsufficientMemoryException memEx) when (
                        cfg.StudioBundlePreset != Cavernize.Logic.Spatial.NeuralEnsembleBundlePreset.FastSingle ||
                        cfg.OrchestrationMode == Cavernize.Logic.Spatial.NeuralOrchestrationMode.Manual) {
                        AppLogger.Warn($"ONNX heavy bundle exceeded safe RAM budget ({memEx.Message}); automatically falling back to memory-safe FastSingle local plan.");
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                        GC.WaitForPendingFinalizers();
                        Cavernize.Logic.Spatial.NeuralAudioConfig liteCfg = cfg.WithMemorySafeFallback();
                        Cavernize.Logic.Spatial.NeuralExecutionPlan litePlan = neuralOrchestrator.BuildPlan(goal, liteCfg);
                        if (litePlan != null && litePlan.IsRunnable && !string.Equals(litePlan.PlanSignature, plan?.PlanSignature, StringComparison.Ordinal)) {
                            reportProgress(0.06, S["MvsepAutoFallbackStatus"]);
                            (separated, rtf, fromCache) = neuralOrchestrator.ExecuteTrackPipeline(
                                sourceTrackKey: activePath,
                                fileSizeBytes: keyInfo.Length,
                                lastModifiedTicks: keyInfo.LastWriteTimeUtc.Ticks,
                                mixLeft: mixL,
                                mixRight: mixR,
                                sampleRate: decodedSr,
                                goal: goal,
                                config: liteCfg,
                                derivedFromLosslessTwin: usedAlignedTwin || derivedFromTwin,
                                onProgress: reportProgress,
                                token: ct,
                                onProgressiveTrackReady: progressiveCallback,
                                displayTitle: summaryTitle,
                                artistName: summaryArtist,
                                forceReextract: forceReextract,
                                preExtractedTrack: reusableStage1);
                        } else {
                            throw;
                        }
                    }
                }

                Cavern.VocalChain.VocalMixDnaProfile extractedDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                    separated, summaryTitle);
                if (extractedDna != null && extractedDna.IsExtractedFromSong) {
                    SaveCachedSongVocalDna(activeTrack, extractedDna);
                }

                if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation || goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                    await BindSpatialStemsOffUiThreadAsync(separated, goal, ct).ConfigureAwait(false);
                }
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) return;
                    isNeuralPrepRunning = false;
                    activeNeuralPrepGoal = null;
                    neuralPrepProgress = 1.0;
                    neuralPrepStatusText = "";
                    SyncNeuralReferenceOffsetForActiveTrack(activeTrack);
                    ApplyExtractedSongDnaOnUiThread(extractedDna);
                    SyncNeuralRuntimeDiagnostics();
                    NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Upmix, immediate: true);
                    string toastMsg;
                    ToastLength toastLen = ToastLength.Short;
                    if (separated.UsedMemorySafeFallback) {
                        string fallbackModel = string.IsNullOrWhiteSpace(separated.ModelSummary) ? "Mobile AI" : separated.ModelSummary;
                        AppLogger.Warn($"Low-RAM fallback completed via '{fallbackModel}': {separated.MemoryFallbackReason}");
                        toastMsg = string.Format(S["NeuralLowRamFallbackToast"], fallbackModel);
                        toastLen = ToastLength.Long;
                    } else if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna && extractedDna != null && extractedDna.IsExtractedFromSong) {
                        toastMsg = string.Format(S["VocalDnaReadyToast"], extractedDna.TargetVocalLufs, extractedDna.ReverbRt60Sec, extractedDna.ActiveWindowCount);
                    } else {
                        toastMsg = fromCache
                            ? string.Format(S["NeuralPrepCacheHitToast"], separated.Stems.Count)
                            : string.Format(S["NeuralPrepDoneToast"], separated.Stems.Count, rtf);
                    }
                    Toast.MakeText(this, toastMsg, toastLen).Show();
                });
            } catch (System.OperationCanceledException) {
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    activeNeuralPrepGoal = null;
                    neuralPrepProgress = 0;
                    neuralPrepStatusText = "";
                    RefreshNeuralPrepUi();
                });
            } catch (ObjectDisposedException) when (ct.IsCancellationRequested || myGen != neuralPrepGeneration) {
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    activeNeuralPrepGoal = null;
                    neuralPrepProgress = 0;
                    neuralPrepStatusText = "";
                    RefreshNeuralPrepUi();
                });
            } catch (InsufficientMemoryException memEx) {
                string safeMsg = Cavernize.Logic.Spatial.MvsepCloudClient.RedactSecrets(memEx.Message, store?.MvsepApiToken);
                AppLogger.Warn($"ONNX low-RAM guard (no lightweight fallback available): {safeMsg}");
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    activeNeuralPrepGoal = null;
                    neuralPrepProgress = 0;
                    neuralPrepStatusText = "";
                    RefreshNeuralPrepUi();
                    Toast.MakeText(this, string.Format(S["NeuralLowRamNeedLightModelFmt"], safeMsg), ToastLength.Long).Show();
                });
            } catch (Exception ex) {
                string safeMsg = Cavernize.Logic.Spatial.MvsepCloudClient.RedactSecrets(ex.Message, store?.MvsepApiToken);
                AppLogger.Warn($"W18/W19.b/T3 PrepareActiveTrackNeuralStemsAsync: {safeMsg}");
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    activeNeuralPrepGoal = null;
                    neuralPrepProgress = 0;
                    neuralPrepStatusText = "";
                    RefreshNeuralPrepUi();
                    Toast.MakeText(this, string.Format(S["NeuralPrepFailedFmt"], safeMsg), ToastLength.Long).Show();
                });
            } finally {
                try {
                    myPrepCts.Dispose();
                } catch { }
            }
        });
    }

    (float[] Left, float[] Right, int SampleRate) DecodeTempStemBytesWithFfmpeg(
        byte[] rawBytes,
        string fileName,
        int preferredSampleRate,
        string ffmpegLoc,
        CancellationToken ct) {
        string ext = Path.GetExtension(fileName ?? "");
        if (string.IsNullOrWhiteSpace(ext)) {
            ext = ".flac";
        }
        string tempPath = Path.Combine(CacheDir?.AbsolutePath ?? FilesDir.AbsolutePath, $"mvsep_dl_{Guid.NewGuid():N}{ext}");
        try {
            File.WriteAllBytes(tempPath, rawBytes);
            if (!LosslessTwinDiscoveryService.TryDecodeTrackToStereoFloat(
                tempPath, preferredSampleRate, ffmpegLoc, ct, out float[] l, out float[] r, out int sr)) {
                throw new InvalidOperationException($"Unable to decode MVSEP stem '{fileName}'.");
            }
            return (l, r, sr);
        } finally {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    void PrepareSelectedTrueHDTrackAsync(CavernizeTrack sourceTrack, bool autoPlayAfter) {
        if (sourceTrack == null || isPreparingTrueHD || uiDestroyed) {
            return;
        }
        string existingBin = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        if (string.IsNullOrEmpty(existingBin) || !File.Exists(existingBin)) {
            ShowError(S["TruehddNotInstalled"]);
            BindActiveTrackToUiAndPlayer(sourceTrack);
            return;
        }

        if (!store.TruehddLicenceAccepted) {
            _ = Task.Run(async () => {
                string licText = await TruehddInstallerService.FetchLicenceTextAsync(this).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed || selectedTrack != sourceTrack) {
                        return;
                    }
                    OneUiModalDialog.Show(
                        this,
                        S["TrueHDLicenceTitle"],
                        $"{S["TrueHDLicenceDesc"]}\n\n{licText}",
                        icon: OneUiIconType.Sparkles,
                        iconBadgeColor: OneUiTheme.Primary,
                        positiveText: S["TrueHDDownloadDecode"],
                        onPositive: () => {
                            store.TruehddLicenceAccepted = true;
                            StartTrueHDPrepareWorker(sourceTrack, autoPlayAfter);
                        },
                        negativeText: S["CancelBtn"],
                        onNegative: () => {
                            SetConvertStatus(S["TrueHDCancelled"], 0);
                            BindActiveTrackToUiAndPlayer(sourceTrack);
                        },
                        cancelable: false);
                });
            });
            return;
        }

        StartTrueHDPrepareWorker(sourceTrack, autoPlayAfter);
    }

    void StartTrueHDPrepareWorker(CavernizeTrack sourceTrack, bool autoPlayAfter) {
        if (uiDestroyed || selectedTrack != sourceTrack) {
            return;
        }
        int myGen = ++truehdPrepareGeneration;
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        truehdPrepareCts = new CancellationTokenSource();
        CancellationToken token = truehdPrepareCts.Token;
        isPreparingTrueHD = true;

        LinearLayout dlgContent = new(this) { Orientation = Orientation.Vertical };
        TextView dlgStatus = Label(S["TrueHDDecoding"], 12.5f, OneUiTheme.TextSecondary);
        dlgStatus.SetPadding(0, 0, 0, Dp(10));
        ProgressBar dlgProgress = new(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Indeterminate = true
        };
        OneUiTheme.StyleProgressBar(this, dlgProgress);
        dlgContent.AddView(dlgStatus);
        dlgContent.AddView(dlgProgress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)));

        Android.App.Dialog progressDlg = OneUiModalDialog.Show(
            this,
            S["TrueHDTitle"],
            customContent: dlgContent,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: null,
            negativeText: S["CancelBtn"],
            onNegative: () => {
                if (myGen == truehdPrepareGeneration) {
                    truehdPrepareGeneration++;
                    isPreparingTrueHD = false;
                }
                truehdPrepareCts?.Cancel();
                SetConvertStatus(S["TrueHDCancelled"], 0);
                BindActiveTrackToUiAndPlayer(sourceTrack);
            },
            cancelable: false);

        void UpdatePrepUi(double p, string msg) {
            RunOnUiThread(() => {
                if (uiDestroyed || myGen != truehdPrepareGeneration) return;
                dlgStatus.Text = msg ?? "";
                if (p < 0) {
                    dlgProgress.Indeterminate = true;
                } else {
                    dlgProgress.Indeterminate = false;
                    dlgProgress.Progress = Math.Clamp((int)Math.Round(p * 1000.0), 0, 1000);
                }
                SetConvertStatus(msg, p);
            });
        }

        Task.Run(() => {
            try {
                string truehddPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
                if (string.IsNullOrEmpty(truehddPath) || !File.Exists(truehddPath)) {
                    throw new FileNotFoundException(S["TruehddNotInstalled"]);
                }
                Truehdd.CustomExecutablePath = truehddPath;

                token.ThrowIfCancellationRequested();
                int? forcedPres = store.MlpPresentation >= 0 ? store.MlpPresentation : null;
                TrueHDDecodeResult res = TrueHDDecoderService.DecodeTrack(
                    this,
                    sourceTrack,
                    LoadedFile?.Path,
                    ffmpeg.Found ? ffmpeg.Location : null,
                    truehddPath,
                    forcedPres,
                    UpdatePrepUi,
                    token);

                RunOnUiThread(() => {
                    if (myGen == truehdPrepareGeneration) {
                        isPreparingTrueHD = false;
                    }
                    try { progressDlg?.Dismiss(); } catch { }
                    if (uiDestroyed || selectedTrack != sourceTrack || myGen != truehdPrepareGeneration) {
                        TrueHDDecoderService.CleanupWorkDirectory(res.SessionDirectory);
                        res.DecodedTrack?.Dispose();
                        return;
                    }
                    decodedTrueHDTrack = res.DecodedTrack;
                    activeTrueHDSessionDir = res.SessionDirectory;
                    SetConvertStatus(S["Ready"], 0);
                    BindActiveTrackToUiAndPlayer(decodedTrueHDTrack);
                    if (autoPlayAfter && decodedTrueHDTrack != null) {
                        TogglePlayPause();
                    }
                });
            } catch (System.OperationCanceledException) {
                AppLogger.Info("TrueHD decode cancelled by user.");
                RunOnUiThread(() => {
                    try { progressDlg?.Dismiss(); } catch { }
                    if (myGen == truehdPrepareGeneration) {
                        isPreparingTrueHD = false;
                        SetConvertStatus(S["TrueHDCancelled"], 0);
                        BindActiveTrackToUiAndPlayer(sourceTrack);
                    }
                });
            } catch (Exception ex) {
                AppLogger.Error("PrepareSelectedTrueHDTrackAsync", ex);
                RunOnUiThread(() => {
                    try { progressDlg?.Dismiss(); } catch { }
                    if (myGen == truehdPrepareGeneration) {
                        isPreparingTrueHD = false;
                        SetConvertStatus(ex.Message, 0);
                        BindActiveTrackToUiAndPlayer(sourceTrack);
                        ShowError(ex.Message);
                    }
                });
            }
        });
    }

}
