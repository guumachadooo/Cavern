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
    void StartLosslessTwinDiscoveryAsync(CavernizeTrack activeTrack, bool forceRescan = false) {
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

        losslessTwinCts?.Cancel();
        losslessTwinCts?.Dispose();
        losslessTwinCts = new CancellationTokenSource();
        CancellationToken ct = losslessTwinCts.Token;
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

        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        string title = currentMediaMeta?.Title ?? "";
        string artist = currentMediaMeta?.Artist ?? "";
        string album = currentMediaMeta?.Album ?? "";
        string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;

        _ = Task.Run(async () => {
            try {
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
                        twin.Dispose();
                        return;
                    }
                    activeLosslessTwin = twin;
                    lastLgsrSyncedPath = twin != null ? activePath : "";
                    lastLgsrSyncedConfigHash = cfgHash;
                    playerEngine.BindLosslessReference(twin);
                    RefreshLgsrUi();
                });
            } catch (System.OperationCanceledException) {
                // Superseded by newer track selection
            } catch (Exception ex) {
                AppLogger.Warn($"W09 StartLosslessTwinDiscoveryAsync: {ex.Message}");
            }
        }, ct);
    }

    void CancelNeuralTrackPrep(bool silent = false) {
        neuralPrepGeneration++;
        try {
            neuralPrepCts?.Cancel();
            neuralPrepCts?.Dispose();
        } catch { }
        neuralPrepCts = null;
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

        OneUiModalDialog.Show(
            this,
            S["PlayTabAiDialogTitle"],
            null,
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconSliderMedia,
            positiveText: S["NeuralModelsBtn"],
            onPositive: ShowNeuralModelsModal,
            negativeText: S["CloseBtn"],
            onHelp: ShowPlayTabSpatialHierarchyHelpDialog,
            helpContentDesc: S["PlayTabSpatialGuideTitle"]);
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

    void TryLoadCachedSongVocalDnaAsync(CavernizeTrack activeTrack) {
        if (uiDestroyed || activeTrack == null || playerEngine == null) {
            return;
        }
        int gen = ++vocalDnaLoadGeneration;
        string cachePath = ResolveVocalDnaCacheFilePath(activeTrack);
        if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) {
            return;
        }
        _ = Task.Run(() => {
            try {
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

    void TryAutoBindCachedNeuralStemsAsync(CavernizeTrack activeTrack) {
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

        _ = Task.Run(() => {
            try {
                FileInfo fi = new(sourceKey);
                Cavernize.Logic.Spatial.NeuralSeparatedTrack spatialUpmixTrack = null;
                Cavernize.Logic.Spatial.NeuralSeparatedTrack karaokeTrack = null;
                Cavernize.Logic.Spatial.NeuralSeparatedTrack dnaTrack = null;

                // W48: Independently auto-bind BOTH Spatial Upmix (full-mix) AND Karaoke caches without clobbering
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
                string summaryTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                    ? currentMediaMeta.Title
                    : Path.GetFileNameWithoutExtension(sourceKey);
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
                        ApplyExtractedSongDnaOnUiThread(extractedDna);
                        RefreshNeuralAudioUi();
                        RefreshNeuralPrepUi();
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
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg) {
        string presetId = preset?.Id ?? "mvsep_default";
        bool pbv = cfg?.KaraokePreserveBackingVocals ?? true;
        return $"mvsep:{presetId}|pbv={(pbv ? 1 : 0)}";
    }

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
        ApplyAndRefreshNeuralAudio();
        RefreshLyricsSingDockUi();
        RefreshStudioW12ContextUi();

        string toastText = nextPreserveBacking
            ? S["KaraokeBackingKeptToast"]
            : S["KaraokePureInstrumentalToast"];
        Toast.MakeText(this, toastText, ToastLength.Short).Show();

        // If user switched to Pure Instrumental while only a 2-stem lead/backing karaoke track is bound
        // (lacking full combined vocals or distinct accompaniment stems), re-separate cleanly when possible.
        if (!nextPreserveBacking && store.SpatialKaraokeEnabled && !isNeuralPrepRunning) {
            Cavernize.Logic.Spatial.NeuralSeparatedTrack activeKara = playerEngine?.NeuralStage?.ActiveKaraokeTrack;
            bool hasFullVocalOrMulti = activeKara != null &&
                activeKara.Stems != null &&
                (activeKara.Stems.Any(s => string.Equals(s.Name, "vocals", StringComparison.OrdinalIgnoreCase)) ||
                 activeKara.Stems.Count >= 4);
            if (activeKara != null && !hasFullVocalOrMulti && GetActiveTrack() != null) {
                PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke, silentIfNotReady: true);
            }
        }
    }

    void PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal goal, bool silentIfNotReady = false) {
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

        neuralPrepCts?.Cancel();
        neuralPrepCts?.Dispose();
        neuralPrepCts = new CancellationTokenSource();
        CancellationToken ct = neuralPrepCts.Token;
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
                // Instant Slot 7B cache hit before decoding PCM
                if (allowCache &&
                    (neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedTrack, ct) ||
                     (cloudRequested && neuralTrackCache.TryLoad(cloudPlainCacheKey, out cachedTrack, ct)))) {
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
                        artistName: summaryArtist);
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
                    if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna && extractedDna != null && extractedDna.IsExtractedFromSong) {
                        toastMsg = string.Format(S["VocalDnaReadyToast"], extractedDna.TargetVocalLufs, extractedDna.ReverbRt60Sec, extractedDna.ActiveWindowCount);
                    } else {
                        toastMsg = fromCache
                            ? string.Format(S["NeuralPrepCacheHitToast"], separated.Stems.Count)
                            : string.Format(S["NeuralPrepDoneToast"], separated.Stems.Count, rtf);
                    }
                    Toast.MakeText(this, toastMsg, ToastLength.Short).Show();
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
            }
        }, ct);
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
