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
    readonly Dictionary<string, (double OffsetSamples, int SampleRate)> crossVariantOffsetByCanonicalSong = new(StringComparer.OrdinalIgnoreCase);

    double ComputeCrossVariantNeuralOffsetSamples(CavernizeTrack targetTrack, string targetPath) {
        if (targetTrack == null || playerEngine?.NeuralStage == null) {
            return 0.0;
        }
        Cavernize.Logic.Spatial.NeuralSeparatedTrack activeSep =
            playerEngine.NeuralStage.ActiveKaraokeTrack ?? playerEngine.NeuralStage.ActiveSpatialUpmixTrack;
        if (activeSep == null) {
            return 0.0;
        }
        bool targetIsAtmos = IsTrackMultichannelOrAtmos(targetTrack, targetPath);
        bool stemsOnAtmosTimeline = activeSep.DerivedFromLosslessTwin ||
            IsPathAtmosOrSpatialContainer(activeSep.SourceTrackKey);
        if (targetIsAtmos == stemsOnAtmosTimeline) {
            return 0.0;
        }

        string canon = NeuralTrackCacheService.ExtractCanonicalSongStem(
            !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : targetPath);
        double baseOffset = 0.0;
        int offsetSr = 48000;
        if (activeLosslessTwin != null && activeLosslessTwin.HasSyncedLosslessTwin) {
            baseOffset = activeLosslessTwin.SyncResult.BaseOffsetSamples;
            offsetSr = activeLosslessTwin.SampleRate >= 8000 ? activeLosslessTwin.SampleRate : 48000;
        } else if (!string.IsNullOrEmpty(canon) && crossVariantOffsetByCanonicalSong.TryGetValue(canon, out var cached)) {
            baseOffset = cached.OffsetSamples;
            offsetSr = cached.SampleRate >= 8000 ? cached.SampleRate : 48000;
        } else {
            return 0.0;
        }

        int outSr = playerEngine.OutputSampleRate >= 8000 ? playerEngine.OutputSampleRate : 48000;
        double scaledOffset = offsetSr == outSr ? baseOffset : baseOffset * (outSr / (double)offsetSr);
        // SubSampleSyncEngine: losslessIndex = BaseOffsetSamples + atmosIndex
        // Atmos stems playing on FLAC -> stemIdx = losslessIdx - BaseOffsetSamples
        // FLAC stems playing on Atmos -> stemIdx = atmosIdx + BaseOffsetSamples
        return stemsOnAtmosTimeline && !targetIsAtmos ? -scaledOffset : scaledOffset;
    }

    void SyncNeuralReferenceOffsetForActiveTrack(CavernizeTrack activeTrack) {
        if (activeTrack == null || playerEngine?.NeuralStage == null) {
            return;
        }
        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        double offset = ComputeCrossVariantNeuralOffsetSamples(activeTrack, activePath);
        playerEngine.NeuralStage.ReferenceSampleOffset = offset;
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
                            crossVariantOffsetByCanonicalSong[canon] = (twin.SyncResult.BaseOffsetSamples, twin.SampleRate >= 8000 ? twin.SampleRate : 48000);
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
            RefreshLgsrUi();
            RefreshNeuralAudioUi();
            RefreshNeuralPrepUi();
            RefreshBottomActionBarVisuals();
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
            RefreshNeuralAudioUi();
            RefreshNeuralPrepUi();
            RefreshBottomActionBarVisuals();
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
        RefreshNeuralAudioUi();
        RefreshNeuralPrepUi();
        RefreshBottomActionBarVisuals();

        if (!(playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems ?? false)) {
            PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation);
        } else {
            string modeName = targetMode == (int)Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.HybridNeuralDirAc
                ? S["PlayTabAiModeHybrid714"]
                : S["PlayTabAiModeStems3D"];
            Toast.MakeText(this, string.Format(S["PlayTabAiOnToastFmt"], modeName), ToastLength.Short).Show();
        }
    }

    void ShowPlayTabAiModeDialog() {
        if (uiDestroyed || store == null) {
            return;
        }
        bool isAtmos = IsActiveTrackMultichannelOrAtmos();
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        if (isAtmos) {
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
                    RefreshLgsrUi();
                    RefreshNeuralPrepUi();
                    RefreshSpatialChoreoClarifierUi();
                    RefreshBottomActionBarVisuals();
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
            modeSel.SelectionChanged += idx => {
                store.NeuralSpatialUpmixModeIndex = idx;
                _ = playerEngine?.ApplyNeuralConfigAsync(store.BuildNeuralAudioConfig());
                RefreshNeuralAudioUi();
                RefreshNeuralPrepUi();
                RefreshSpatialChoreoClarifierUi();
                RefreshBottomActionBarVisuals();
                if (idx != 0 && !(playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems ?? false) && GetActiveTrack() != null) {
                    PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, silentIfNotReady: false);
                }
            };
            content.AddView(WrapControlWithHelp(modeSel, S["PlayTabAiModeTitle"], S["TipNeuralUpmix"], bottomMarginDp: 2));
        }

        Android.App.Dialog activeDialog = null;
        AddSongAiCacheInspectorSection(
            content,
            Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation,
            () => activeDialog?.Dismiss());

        activeDialog = OneUiModalDialog.Show(
            this,
            S["PlayTabAiDialogTitle"],
            null,
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconSliderMedia,
            positiveText: S["OpenAiSettingsBtn"],
            onPositive: () => JumpToSettingsCategory(2),
            negativeText: S["CloseBtn"],
            onHelp: ShowPlayTabSpatialHierarchyHelpDialog,
            helpContentDesc: S["PlayTabSpatialGuideTitle"]);
    }

    void ShowKaraokeCacheInspectorDialog() {
        if (uiDestroyed || store == null) {
            return;
        }
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog activeDialog = null;
        AddSongAiCacheInspectorSection(
            content,
            Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke,
            () => activeDialog?.Dismiss());

        activeDialog = OneUiModalDialog.Show(
            this,
            S["KaraokeCacheDialogTitle"],
            null,
            customContent: content,
            icon: OneUiIconType.Microphone,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["OpenAiSettingsBtn"],
            onPositive: () => JumpToSettingsCategory(2),
            negativeText: S["CloseBtn"]);
    }

    string FormatCacheDiffFieldName(string fieldKey) => fieldKey switch {
        "Backend" => S["AiCacheDiffFieldBackend"],
        "Models" => S["AiCacheDiffFieldModels"],
        "Bundle" => S["AiCacheDiffFieldBundle"],
        "Orchestration" => S["AiCacheDiffFieldOrchestration"],
        "Overlap" => S["AiCacheDiffFieldOverlap"],
        "TTA" => S["AiCacheDiffFieldTta"],
        "Ensemble" => S["AiCacheDiffFieldEnsemble"],
        "PhaseFix" => S["AiCacheDiffFieldPhaseFix"],
        "EnergyConservation" => S["AiCacheDiffFieldEnergyConservation"],
        "BackingVocals" => S["AiCacheDiffFieldBackingVocals"],
        _ => fieldKey ?? ""
    };

    string FormatCacheDiffValue(string fieldKey, string rawVal) {
        if (string.Equals(fieldKey, "BackingVocals", StringComparison.OrdinalIgnoreCase)) {
            if (string.Equals(rawVal, "KeepBacking", StringComparison.OrdinalIgnoreCase)) return S["AiCacheDiffValKeepBacking"];
            if (string.Equals(rawVal, "PureInstrumental", StringComparison.OrdinalIgnoreCase)) return S["AiCacheDiffValPureInstr"];
        }
        return rawVal ?? "—";
    }

    (Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo BestEntry, Cavernize.Logic.Spatial.NeuralCacheConfigComparison Comparison) InspectActiveSongCacheState(
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal) {
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || neuralTrackCache == null || store == null) {
            return (null, null);
        }
        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileNameWithoutExtension(activePath);
        string artist = (currentMediaMeta?.Artist ?? "").Trim();

        IReadOnlyList<Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo> matches =
            neuralTrackCache.FindEntriesForTrack(activePath, title, artist, goal);
        if (matches == null || matches.Count == 0) {
            return (null, null);
        }

        Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo best = matches[0];
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        bool usesCloud = CanUseMvsepCloud(cfg);
        Cavernize.Logic.Spatial.NeuralExecutionPlan localPlan = neuralOrchestrator?.BuildPlan(goal, cfg);
        string cloudPresetId = usesCloud ? ResolveMvsepPresetForGoal(goal, cfg)?.Id : null;
        Cavernize.Logic.Spatial.NeuralCacheConfigComparison cmp = Cavernize.Logic.Spatial.NeuralCacheConfigDiff.Compare(
            best, cfg, localPlan, cloudPresetId, usesCloud, goal);
        return (best, cmp);
    }

    void AddSongAiCacheInspectorSection(
        LinearLayout parent,
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal,
        Action dismissDialog) {
        if (parent == null || store == null) {
            return;
        }
        CavernizeTrack activeTrack = GetActiveTrack();
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        card.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.WithAlpha(OneUiTheme.SurfaceElevated, 215), 16f, OneUiTheme.Divider, 1f);

        TextView titleTv = Label(S["AiCacheSectionTitle"], 12.6f, OneUiTheme.TextPrimary);
        titleTv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
        card.AddView(titleTv);

        TextView sharedNote = Label(S["AiCacheSharedNote"], 11.2f, OneUiTheme.TextSub);
        sharedNote.SetPadding(0, Dp(2), 0, Dp(6));
        card.AddView(sharedNote);

        if (activeTrack == null) {
            card.AddView(Label(S["NeuralPrepNoTrackToast"], 11.8f, OneUiTheme.TextSecondary));
        } else {
            (Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo best, Cavernize.Logic.Spatial.NeuralCacheConfigComparison cmp) =
                InspectActiveSongCacheState(goal);

            if (best == null) {
                TextView emptyTv = Label(S["AiCacheStatusNotCached"], 12f, OneUiTheme.TextSecondary);
                emptyTv.SetPadding(0, Dp(2), 0, Dp(8));
                card.AddView(emptyTv);

                Button extractBtn = PillButton(S["AiCacheExtractNowBtn"], () => {
                    dismissDialog?.Invoke();
                    PrepareActiveTrackNeuralStemsAsync(goal, silentIfNotReady: false, forceReextract: false);
                }, active: true);
                extractBtn.TextSize = 11.8f;
                card.AddView(extractBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            } else {
                double sizeMb = best.CompressedBytes / (1024.0 * 1024.0);
                string formatBadge = !string.IsNullOrWhiteSpace(cmp?.SourceFormatLabel)
                    ? cmp.SourceFormatLabel
                    : Cavernize.Logic.Spatial.NeuralCacheConfigDiff.ExtractSourceFormatLabel(best.SourceTrackKey);
                TextView statusTv = Label(
                    string.Format(S["AiCacheStatusCachedFmt"], best.StemCount, sizeMb, formatBadge),
                    12.2f,
                    OneUiTheme.FunctionalGreen);
                statusTv.SetTypeface(OneUiTheme.GetAppleFontMedium(this), TypefaceStyle.Normal);
                card.AddView(statusTv);

                bool matches = cmp == null || cmp.MatchesCurrentConfig;
                TextView matchTv = Label(
                    matches ? S["AiCacheConfigMatches"] : S["AiCacheConfigDiffersHeader"],
                    11.8f,
                    matches ? OneUiTheme.PrimaryHigh : OneUiTheme.FunctionalOrange);
                matchTv.SetPadding(0, Dp(4), 0, Dp(4));
                matchTv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
                card.AddView(matchTv);

                if (!matches && cmp?.Differences != null && cmp.Differences.Count > 0) {
                    LinearLayout diffBox = new(this) { Orientation = Orientation.Vertical };
                    diffBox.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
                    diffBox.Background = OneUiTheme.CreateRoundedDrawable(
                        this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 26), 12f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 96), 0.9f);
                    for (int i = 0; i < cmp.Differences.Count; i++) {
                        Cavernize.Logic.Spatial.NeuralCacheConfigDiffItem d = cmp.Differences[i];
                        string fieldLabel = FormatCacheDiffFieldName(d.FieldKey);
                        string oldVal = FormatCacheDiffValue(d.FieldKey, d.CachedValue);
                        string newVal = FormatCacheDiffValue(d.FieldKey, d.CurrentValue);
                        TextView lineTv = Label($"• {fieldLabel}: {oldVal}  →  {newVal}", 11.4f, OneUiTheme.TextPrimary);
                        if (i > 0) {
                            lineTv.SetPadding(0, Dp(2), 0, 0);
                        }
                        diffBox.AddView(lineTv);
                    }
                    card.AddView(diffBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        BottomMargin = Dp(8)
                    });
                }

                Button reextractBtn = PillButton(S["AiCacheReextractCurrentBtn"], () => {
                    dismissDialog?.Invoke();
                    PrepareActiveTrackNeuralStemsAsync(goal, silentIfNotReady: false, forceReextract: true);
                }, active: !matches);
                reextractBtn.TextSize = 11.8f;
                card.AddView(reextractBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(4)
                });

                Button removeBtn = PillButton(S["AiCacheRemoveBtn"], () => {
                    string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
                    string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                        ? currentMediaMeta.Title
                        : Path.GetFileNameWithoutExtension(activePath);
                    string artist = (currentMediaMeta?.Artist ?? "").Trim();
                    neuralTrackCache?.DeleteEntriesForTrack(activePath, title, artist);
                    playerEngine?.NeuralStage?.ClearAllSeparatedTracks();
                    playerEngine?.BindSeparatedNeuralTrack(null);
                    SyncNeuralRuntimeDiagnostics();
                    RefreshNeuralAudioUi();
                    RefreshNeuralPrepUi();
                    RefreshLyricsSingDockUi();
                    RefreshBottomActionBarVisuals();
                    Toast.MakeText(this, S["AiCacheRemovedToast"], ToastLength.Short).Show();
                    dismissDialog?.Invoke();
                });
                removeBtn.TextSize = 11.6f;
                removeBtn.SetTextColor(OneUiTheme.FunctionalRed);
                card.AddView(removeBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(6)
                });
            }
        }

        parent.AddView(card, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(10)
        });
    }

    void ShowPlayTabSpatialUpmixDialog() {
        if (uiDestroyed || store == null) {
            return;
        }
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView desc = Label(S["PlayTabUpmixDialogDesc"], 12.4f, OneUiTheme.TextSecondary);
        desc.SetPadding(0, 0, 0, Dp(10));
        content.AddView(desc);

        OneUiInlineSelector sel = new(this, S["UpmixModeLabel"]);
        sel.SetItems(
            [S["UpmixModeDirect"], S["UpmixModeSurround"], S["UpmixModeFull3D"]],
            ResolveUpmixModeIndex());
        sel.SelectionChanged += pos => {
            store.MatrixUpmixing = pos >= 1;
            store.CavernizeUpmix = pos >= 2;
            SyncUpmixModeUi();
            _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
            RefreshSpatialChoreoClarifierUi();
            RefreshMusicPlayerControlsUi();
            RefreshSettingsCategorySubtitles();
        };
        content.AddView(WrapControlWithHelp(sel, S["UpmixModeLabel"].TrimEnd(':'), S["TipUpmixMode"], bottomMarginDp: 6));

        AddDivider(content);
        AddSwitchRow(content, S["AtmosAwareUpmix"], store.AtmosAwareUpmix, v => {
            store.AtmosAwareUpmix = v;
            if (atmosAwareUpmixSwitch != null && atmosAwareUpmixSwitch.Checked != v) {
                atmosAwareUpmixSwitch.Checked = v;
            }
            _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
            RefreshMusicPlayerControlsUi();
            RefreshSettingsCategorySubtitles();
        }, helpBody: S["TipAtmosAwareUpmix"]);

        OneUiModalDialog.Show(
            this,
            S["UpmixTitle"],
            null,
            customContent: content,
            icon: OneUiIconType.HeightUp,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["CloseBtn"],
            onHelp: ShowPlayTabSpatialHierarchyHelpDialog,
            helpContentDesc: S["PlayTabSpatialGuideTitle"]);
    }

    void ShowPlayTabSpatialHierarchyHelpDialog() {
        if (uiDestroyed) {
            return;
        }
        OneUiModalDialog.Show(
            this,
            S["PlayTabSpatialGuideTitle"],
            S["PlayTabSpatialGuideBody"],
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["OkBtn"]);
    }

    sealed class CachedVocalDnaRecord {
        public float[] BandEnergyDb { get; set; }
        public float SpectralTiltDbPerOct { get; set; }
        public float SibilanceIndexDb { get; set; }
        public float TargetVocalLufs { get; set; }
        public float CrestFactorDb { get; set; }
        public float HarmonicWarmth { get; set; }
        public float ReverbRt60Sec { get; set; }
        public float ReverbPreDelayMs { get; set; }
        public float ReverbWetMix { get; set; }
        public float ReverbDampingHz { get; set; }
        public float DelayTimeMs { get; set; }
        public float DelayWetMix { get; set; }
        public float StereoWidth { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public bool HasMeasuredDereverb { get; set; }
        public float? MeasuredDrrDb { get; set; }
        public float ActiveSpeechRmsDbfs { get; set; }
        public int ActiveWindowCount { get; set; }
        public string SourceTrackSummary { get; set; }
    }

    string ResolveVocalDnaCacheFilePath(CavernizeTrack track) {
        string sourceKey = track?.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? track?.Path ?? "";
        if (string.IsNullOrEmpty(sourceKey) || !File.Exists(sourceKey)) {
            return null;
        }
        try {
            FileInfo fi = new(sourceKey);
            string raw = $"{sourceKey}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|dna_v2";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            string hex = Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
            string dir = Path.Combine(CacheDir?.AbsolutePath ?? FilesDir.AbsolutePath, "vocal_dna");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{hex}.json");
        } catch {
            return null;
        }
    }

    void SaveCachedSongVocalDna(CavernizeTrack track, Cavern.VocalChain.VocalMixDnaProfile dna) {
        if (track == null || dna == null || !dna.IsExtractedFromSong) {
            return;
        }
        string cachePath = ResolveVocalDnaCacheFilePath(track);
        if (string.IsNullOrEmpty(cachePath)) {
            return;
        }
        try {
            CachedVocalDnaRecord rec = new() {
                BandEnergyDb = dna.BandEnergyDb,
                SpectralTiltDbPerOct = dna.SpectralTiltDbPerOct,
                SibilanceIndexDb = dna.SibilanceIndexDb,
                TargetVocalLufs = dna.TargetVocalLufs,
                CrestFactorDb = dna.CrestFactorDb,
                HarmonicWarmth = dna.HarmonicWarmth,
                ReverbRt60Sec = dna.ReverbRt60Sec,
                ReverbPreDelayMs = dna.ReverbPreDelayMs,
                ReverbWetMix = dna.ReverbWetMix,
                ReverbDampingHz = dna.ReverbDampingHz,
                DelayTimeMs = dna.DelayTimeMs,
                DelayWetMix = dna.DelayWetMix,
                StereoWidth = dna.StereoWidth,
                PosX = dna.VocalAtmosPosition.X,
                PosY = dna.VocalAtmosPosition.Y,
                PosZ = dna.VocalAtmosPosition.Z,
                HasMeasuredDereverb = dna.HasMeasuredDereverb,
                MeasuredDrrDb = float.IsFinite(dna.MeasuredDrrDb) ? dna.MeasuredDrrDb : null,
                ActiveSpeechRmsDbfs = dna.ActiveSpeechRmsDbfs,
                ActiveWindowCount = dna.ActiveWindowCount,
                SourceTrackSummary = dna.SourceTrackSummary ?? ""
            };
            string json = System.Text.Json.JsonSerializer.Serialize(rec);
            File.WriteAllText(cachePath, json);
        } catch (Exception ex) {
            AppLogger.Warn($"W19.b SaveCachedSongVocalDna: {ex.Message}");
        }
    }

    void TryLoadCachedSongVocalDnaAsync(CavernizeTrack activeTrack, int startupDelayMs = 0) {
        if (uiDestroyed || activeTrack == null || playerEngine == null) {
            return;
        }
        int gen = ++vocalDnaLoadGeneration;
        string cachePath = ResolveVocalDnaCacheFilePath(activeTrack);
        if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                if (startupDelayMs > 0) {
                    await Task.Delay(startupDelayMs).ConfigureAwait(false);
                }
                if (uiDestroyed || gen != vocalDnaLoadGeneration) {
                    return;
                }
                try {
                    Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background);
                } catch {
                    // Best-effort QoS
                }
                string json = File.ReadAllText(cachePath);
                CachedVocalDnaRecord rec = System.Text.Json.JsonSerializer.Deserialize<CachedVocalDnaRecord>(json);
                if (rec == null || rec.BandEnergyDb == null || rec.BandEnergyDb.Length != Cavern.VocalChain.VocalMixDnaProfile.AnalysisBandHz.Length) {
                    return;
                }
                Cavern.VocalChain.VocalMixDnaProfile dna = new() {
                    BandEnergyDb = rec.BandEnergyDb,
                    SpectralTiltDbPerOct = rec.SpectralTiltDbPerOct,
                    SibilanceIndexDb = rec.SibilanceIndexDb,
                    TargetVocalLufs = rec.TargetVocalLufs,
                    CrestFactorDb = rec.CrestFactorDb,
                    HarmonicWarmth = rec.HarmonicWarmth,
                    ReverbRt60Sec = rec.ReverbRt60Sec,
                    ReverbPreDelayMs = rec.ReverbPreDelayMs,
                    ReverbWetMix = rec.ReverbWetMix,
                    ReverbDampingHz = rec.ReverbDampingHz,
                    DelayTimeMs = rec.DelayTimeMs,
                    DelayWetMix = rec.DelayWetMix,
                    StereoWidth = rec.StereoWidth,
                    VocalAtmosPosition = new System.Numerics.Vector3(rec.PosX, rec.PosY, rec.PosZ),
                    IsExtractedFromSong = true,
                    HasMeasuredDereverb = rec.HasMeasuredDereverb,
                    MeasuredDrrDb = rec.MeasuredDrrDb ?? float.NaN,
                    ActiveSpeechRmsDbfs = rec.ActiveSpeechRmsDbfs,
                    ActiveWindowCount = rec.ActiveWindowCount,
                    SourceTrackSummary = rec.SourceTrackSummary ?? ""
                };
                RunOnUiThread(() => {
                    if (uiDestroyed || gen != vocalDnaLoadGeneration || GetActiveTrack() != activeTrack) {
                        return;
                    }
                    ApplyExtractedSongDnaOnUiThread(dna);
                });
            } catch (Exception ex) {
                AppLogger.Warn($"W19.b TryLoadCachedSongVocalDnaAsync: {ex.Message}");
            }
        });
    }

    void ApplyExtractedSongDnaOnUiThread(Cavern.VocalChain.VocalMixDnaProfile dna) {
        if (dna == null || !dna.IsExtractedFromSong || playerEngine?.VocalMixMatchStage == null) {
            return;
        }
        Cavern.VocalChain.VocalMixDnaProfile current = playerEngine.VocalMixMatchStage.ActiveDna;
        if (current.IsExtractedFromSong && current.HasMeasuredDereverb && !dna.HasMeasuredDereverb) {
            return;
        }
        playerEngine.VocalMixMatchStage.ApplySongDna(dna);
        RefreshLyricsSingDockUi();
        RefreshSpatialXRayHud();
    }

    void PrepareSongVocalDnaAsync() {
        if (uiDestroyed) {
            return;
        }
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || !activeTrack.Supported || activeTrack.Codec == Codec.TrueHD) {
            ShowToast(S["NeuralPrepNoTrackToast"]);
            return;
        }
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(
            Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna, cfg);
        bool runtimeReady = neuralRuntime != null && neuralRuntime.IsAvailable;
        if (!runtimeReady || plan == null || !plan.IsRunnable) {
            ShowToast(S["VocalDnaNeedModelsToast"]);
            return;
        }
        PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna);
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

                Cavernize.Logic.Spatial.NeuralSeparatedTrack dnaSource = dnaTrack ?? karaokeTrack ?? spatialUpmixTrack;
                Cavern.VocalChain.VocalMixDnaProfile extractedDna = dnaSource != null
                    ? Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(dnaSource, summaryTitle)
                    : null;
                if (extractedDna != null && extractedDna.IsExtractedFromSong) {
                    SaveCachedSongVocalDna(activeTrack, extractedDna);
                }

                if (spatialUpmixTrack != null || karaokeTrack != null || extractedDna != null) {
                    RunOnUiThread(() => {
                        if (uiDestroyed || gen != neuralPrepGeneration || GetActiveTrack() != activeTrack) {
                            return;
                        }
                        if (spatialUpmixTrack != null) {
                            playerEngine.BindSpatialUpmixTrack(spatialUpmixTrack);
                        }
                        if (karaokeTrack != null) {
                            playerEngine.BindKaraokeReferenceTrack(karaokeTrack);
                        }
                        SyncNeuralReferenceOffsetForActiveTrack(activeTrack);
                        ApplyExtractedSongDnaOnUiThread(extractedDna);
                        RefreshNeuralAudioUi();
                        RefreshNeuralPrepUi();
                        RefreshLyricsSingDockUi();
                    });
                }
            } catch (Exception ex) {
                AppLogger.Warn($"W18/W19.b/W48 TryAutoBindCachedNeuralStemsAsync: {ex.Message}");
            }
        });
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
                                RefreshNeuralAudioUi();
                                RefreshNeuralPrepUi();
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
                // (bypassed only when the user explicitly requests "Re-extract Stems").
                if (allowCache && !forceReextract &&
                    (neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedTrack, ct) ||
                     (cloudRequested && neuralTrackCache.TryLoad(cloudPlainCacheKey, out cachedTrack, ct)) ||
                     neuralTrackCache.TryLoadBestForSourceTrack(activePath, summaryTitle, summaryArtist, goal, out cachedTrack, ct))) {
                    Cavern.VocalChain.VocalMixDnaProfile cachedDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                        cachedTrack, summaryTitle);
                    if (cachedDna != null && cachedDna.IsExtractedFromSong) {
                        SaveCachedSongVocalDna(activeTrack, cachedDna);
                    }
                    RunOnUiThread(() => {
                        if (uiDestroyed || myGen != neuralPrepGeneration) {
                            return;
                        }
                        isNeuralPrepRunning = false;
                        activeNeuralPrepGoal = null;
                        neuralPrepProgress = 1.0;
                        neuralPrepStatusText = "";
                        if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation) {
                            playerEngine.BindSpatialUpmixTrack(cachedTrack);
                        } else if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                            playerEngine.BindKaraokeReferenceTrack(cachedTrack);
                        }
                        ApplyExtractedSongDnaOnUiThread(cachedDna);
                        RefreshNeuralAudioUi();
                        RefreshNeuralPrepUi();
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

                if (cloudRequested) {
                    try {
                        System.Diagnostics.Stopwatch cloudSw = System.Diagnostics.Stopwatch.StartNew();
                        using Cavernize.Logic.Spatial.MvsepCloudClient mvsepClient = new();
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
                            token: ct).ConfigureAwait(false);
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
                            ? progressiveTrack => RunOnUiThread(() => {
                                if (uiDestroyed || myGen != neuralPrepGeneration || progressiveTrack == null) {
                                    return;
                                }
                                if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation) {
                                    playerEngine.BindSpatialUpmixTrack(progressiveTrack);
                                } else if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                                    playerEngine.BindKaraokeReferenceTrack(progressiveTrack);
                                }
                                RefreshNeuralAudioUi();
                                RefreshNeuralPrepUi();
                            })
                            : null;

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
                        forceReextract: forceReextract);
                }

                Cavern.VocalChain.VocalMixDnaProfile extractedDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                    separated, summaryTitle);
                if (extractedDna != null && extractedDna.IsExtractedFromSong) {
                    SaveCachedSongVocalDna(activeTrack, extractedDna);
                }

                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    activeNeuralPrepGoal = null;
                    neuralPrepProgress = 1.0;
                    neuralPrepStatusText = "";
                    // VocalMixDna analyzes a smart <=40s excerpt, so never bind it as a full-length playback stem set
                    if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation) {
                        playerEngine.BindSpatialUpmixTrack(separated);
                    } else if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                        playerEngine.BindKaraokeReferenceTrack(separated);
                    }
                    ApplyExtractedSongDnaOnUiThread(extractedDna);
                    SyncNeuralRuntimeDiagnostics();
                    RefreshNeuralAudioUi();
                    RefreshNeuralPrepUi();
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
