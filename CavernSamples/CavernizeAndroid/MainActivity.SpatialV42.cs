using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Format.Common;
using Cavern.Spatial;

using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public sealed partial class MainActivity {
    LinearLayout spatialChoreoContainer;
    readonly EarCandyMicroNode[] roomEarCandyScratch = new EarCandyMicroNode[EarCandySpectralDetector.MaxMicroObjects];

    CancellationTokenSource startupWarmupCts;
    CancellationTokenSource trackBindCts;
    CancellationTokenSource choreoBuildCts;
    int trackBindGeneration;

    string lastPushedLyricMoodSig = string.Empty;
    LyricMoodDigest[] cachedLyricMoodDigests = Array.Empty<LyricMoodDigest>();

    bool Has3DSpatializationForChoreo() {
        if (store == null) {
            return false;
        }
        return store.HeadphoneVirtualizer ||
            store.CavernizeUpmix ||
            store.MatrixUpmixing ||
            IsActiveTrackMultichannelOrAtmos() ||
            IsPlayTabAiActive() ||
            (playerEngine?.IsLiveNeuralStemPlaybackActive == true) ||
            (playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems == true);
    }

    void RefreshSpatialChoreoClarifierV42() {
        if (uiDestroyed || store == null) {
            return;
        }
        bool has3D = Has3DSpatializationForChoreo();
        SpatialChoreoMode mode = (SpatialChoreoMode)Math.Clamp(store.SpatialChoreoModeIndex, 0, 4);

        if (spatialChoreoContainer != null) {
            ViewStates nextVis = has3D ? ViewStates.Visible : ViewStates.Gone;
            if (spatialChoreoContainer.Visibility != nextVis) {
                spatialChoreoContainer.Visibility = nextVis;
            }
        }

        if (!has3D) {
            if (spatialChoreoTierBadgeText != null) {
                spatialChoreoTierBadgeText.Visibility = ViewStates.Gone;
            }
            if (playerEngine?.SceneHub?.ActiveChoreoTrack != null) {
                choreoBuildGeneration++;
                lastChoreoBuiltStemTrack = null;
                lastChoreoBuiltMode = SpatialChoreoMode.Off;
                Cavern.Neural.NeuralStemSpatializer.SetChoreoTrack(null);
                playerEngine.SceneHub.SetChoreoTrack(null);
                playerEngine.SyncChoreographyPositions();
            }
            return;
        }

        if (spatialChoreoTierBadgeText == null) {
            return;
        }

        if (mode == SpatialChoreoMode.Off) {
            spatialChoreoTierBadgeText.Visibility = ViewStates.Gone;
            return;
        }

        spatialChoreoTierBadgeText.Visibility = ViewStates.Visible;
        string motionDesc = mode switch {
            SpatialChoreoMode.Subtle => S["SpatialChoreoDescSubtle"],
            SpatialChoreoMode.Musical => S["SpatialChoreoDescMusical"],
            SpatialChoreoMode.Cinematic => S["SpatialChoreoDescCinematic"],
            SpatialChoreoMode.Orbit => S["SpatialChoreoDescOrbit"],
            _ => S["SpatialChoreoDescOff"]
        };

        string tierLabel;
        if (IsActiveTrackMultichannelOrAtmos()) {
            tierLabel = store.PlayTabAtmosAiEnhancementEnabled ? S["SpatialTierAtmosAi"] : S["SpatialTierNativeAtmos"];
        } else if (IsPlayTabAiActive()) {
            tierLabel = store.NeuralSpatialUpmixModeIndex == (int)NeuralSpatialUpmixMode.HybridNeuralDirAc
                ? S["SpatialTierAiHybrid714"]
                : S["SpatialTierAiStems3D"];
        } else {
            int upmixIdx = ResolveUpmixModeIndex();
            tierLabel = upmixIdx switch {
                2 => S["SpatialTierCavernize3D"],
                1 => S["SpatialTierMatrix71"],
                _ => S["SpatialTierDirect"]
            };
        }

        spatialChoreoTierBadgeText.Text = string.Format(S["SpatialChoreoClarifierFmt"], tierLabel, motionDesc);
    }

    void SyncSpatialChoreoTrackIfNeededV42(bool forceRebuild = false) {
        if (uiDestroyed || store == null || playerEngine == null) {
            return;
        }
        bool has3D = Has3DSpatializationForChoreo();
        SpatialChoreoMode mode = has3D
            ? (SpatialChoreoMode)Math.Clamp(store.SpatialChoreoModeIndex, 0, 4)
            : SpatialChoreoMode.Off;

        if (playerEngine.SceneHub != null) {
            playerEngine.SceneHub.ActiveChoreoMode = mode;
        }
        Cavern.Neural.NeuralStemSpatializer.ActiveChoreoMode = mode;
        if (spatialChoreoSelector != null && has3D && spatialChoreoSelector.SelectedIndex != (int)mode) {
            spatialChoreoSelector.SetSelection((int)mode, notify: false);
        }

        if (!has3D || mode == SpatialChoreoMode.Off) {
            choreoBuildCts?.Cancel();
            if (lastChoreoBuiltMode != SpatialChoreoMode.Off || playerEngine.SceneHub?.ActiveChoreoTrack != null) {
                lastChoreoBuiltStemTrack = null;
                lastChoreoBuiltMode = SpatialChoreoMode.Off;
                lastChoreoBuiltLyricsSig = string.Empty;
                Cavern.Neural.NeuralStemSpatializer.SetChoreoTrack(null);
                playerEngine.SceneHub?.SetChoreoTrack(null);
                playerEngine.SyncChoreographyPositions();
            }
            return;
        }

        NeuralSeparatedTrack sep = playerEngine.NeuralStage?.ActiveSpatialUpmixTrack;
        if (sep == null || sep.Stems == null || sep.Stems.Count == 0 || sep.IsProgressiveStreaming) {
            if (lastChoreoBuiltStemTrack != null || playerEngine.SceneHub?.ActiveChoreoTrack != null) {
                lastChoreoBuiltStemTrack = null;
                lastChoreoBuiltLyricsSig = string.Empty;
                Cavern.Neural.NeuralStemSpatializer.SetChoreoTrack(null);
                playerEngine.SceneHub?.SetChoreoTrack(null);
                playerEngine.SyncChoreographyPositions();
            }
            return;
        }

        LyricsDocument lyricsDoc = currentMediaMeta?.Lyrics;
        string lyricsSig = BuildLyricsSignature(lyricsDoc);

        if (!forceRebuild &&
            ReferenceEquals(lastChoreoBuiltStemTrack, sep) &&
            lastChoreoBuiltMode == mode &&
            string.Equals(lastChoreoBuiltLyricsSig, lyricsSig, StringComparison.Ordinal) &&
            playerEngine.SceneHub?.ActiveChoreoTrack != null) {
            return;
        }

        if (!forceRebuild &&
            string.IsNullOrEmpty(lyricsSig) &&
            neuralTrackCache != null &&
            neuralTrackCache.TryGetAttachedChoreoTrack(sep, out SpatialChoreoTrack attached) &&
            attached != null &&
            attached.Mode == mode) {
            lastChoreoBuiltStemTrack = sep;
            lastChoreoBuiltMode = mode;
            lastChoreoBuiltLyricsSig = lyricsSig;
            Cavern.Neural.NeuralStemSpatializer.SetChoreoTrack(attached);
            playerEngine.SceneHub?.SetChoreoTrack(attached);
            playerEngine.SyncChoreographyPositions();
            UpdateRoomCanvas();
            return;
        }

        choreoBuildCts?.Cancel();
        choreoBuildCts?.Dispose();
        choreoBuildCts = new CancellationTokenSource();
        CancellationToken ct = choreoBuildCts.Token;

        int myGen = ++choreoBuildGeneration;
        lastChoreoBuiltStemTrack = sep;
        lastChoreoBuiltMode = mode;
        lastChoreoBuiltLyricsSig = lyricsSig;
        int sr = sep.SampleRate >= 8000 ? sep.SampleRate : (playerEngine.OutputSampleRate >= 8000 ? playerEngine.OutputSampleRate : 48000);
        LyricsDocument docSnap = lyricsDoc;
        string sigSnap = lyricsSig;

        _ = Task.Run(() => {
            try {
                ct.ThrowIfCancellationRequested();
                LyricMoodDigest[] moodDigests = ResolveOrExtractMoodDigests(docSnap, sigSnap);
                playerEngine?.SetLyricMoodDigests(moodDigests);
                ct.ThrowIfCancellationRequested();

                SpatialChoreoTrack built = Cavern.Neural.NeuralStemSpatializer.BuildAndSetChoreoTrack(
                    sep.Stems,
                    sr,
                    mode,
                    moodDigests);
                if (built == null || ct.IsCancellationRequested) {
                    return;
                }
                neuralTrackCache?.AttachChoreoTrack(sep, built);
                RunOnUiThread(() => {
                    if (uiDestroyed ||
                        ct.IsCancellationRequested ||
                        myGen != choreoBuildGeneration ||
                        !ReferenceEquals(playerEngine?.NeuralStage?.ActiveSpatialUpmixTrack, sep) ||
                        (SpatialChoreoMode)Math.Clamp(store?.SpatialChoreoModeIndex ?? 0, 0, 4) != mode ||
                        !Has3DSpatializationForChoreo()) {
                        return;
                    }
                    Cavern.Neural.NeuralStemSpatializer.SetChoreoTrack(built);
                    playerEngine.SceneHub?.SetChoreoTrack(built);
                    playerEngine.SyncChoreographyPositions();
                    UpdateRoomCanvas();
                });
            } catch (System.OperationCanceledException) {
            } catch (Exception ex) {
                AppLogger.Warn($"SyncSpatialChoreoTrackIfNeededV42: {ex.Message}");
            }
        }, ct);
    }

    static string BuildLyricsSignature(LyricsDocument doc) {
        if (doc == null || !doc.HasLyrics) {
            return string.Empty;
        }
        return $"{doc.SourceLabel}:{doc.Lines?.Count ?? 0}:{doc.RawUnsyncedText?.Length ?? 0}";
    }

    LyricMoodDigest[] ResolveOrExtractMoodDigests(LyricsDocument doc, string sig) {
        if (doc == null || !doc.HasLyrics || string.IsNullOrEmpty(sig)) {
            lastPushedLyricMoodSig = string.Empty;
            cachedLyricMoodDigests = Array.Empty<LyricMoodDigest>();
            return cachedLyricMoodDigests;
        }
        if (string.Equals(sig, lastPushedLyricMoodSig, StringComparison.Ordinal) && cachedLyricMoodDigests.Length > 0) {
            return cachedLyricMoodDigests;
        }
        LyricMoodDigest[] extracted = LyricsService.ExtractMoodDigest(doc) ?? Array.Empty<LyricMoodDigest>();
        cachedLyricMoodDigests = extracted;
        lastPushedLyricMoodSig = sig;
        return extracted;
    }

    void SyncTrackLyricsMoodToEngine(bool forceRebuildChoreo = false) {
        if (uiDestroyed || playerEngine == null) {
            return;
        }
        LyricsDocument doc = currentMediaMeta?.Lyrics;
        string sig = BuildLyricsSignature(doc);
        if (string.IsNullOrEmpty(sig)) {
            lastPushedLyricMoodSig = string.Empty;
            cachedLyricMoodDigests = Array.Empty<LyricMoodDigest>();
            playerEngine.SetLyricMoodDigests(null);
            if (forceRebuildChoreo) {
                SyncSpatialChoreoTrackIfNeeded(forceRebuild: false);
            }
            return;
        }

        _ = Task.Run(() => {
            try {
                LyricMoodDigest[] digests = ResolveOrExtractMoodDigests(doc, sig);
                playerEngine?.SetLyricMoodDigests(digests);
                if (forceRebuildChoreo) {
                    RunOnUiThread(() => {
                        if (!uiDestroyed && currentMediaMeta?.Lyrics == doc) {
                            SyncSpatialChoreoTrackIfNeeded(forceRebuild: true);
                        }
                    });
                }
            } catch (Exception ex) {
                AppLogger.Warn($"SyncTrackLyricsMoodToEngine: {ex.Message}");
            }
        });
    }

    void NotifyLyricsMetadataUpdated() {
        SyncTrackLyricsMoodToEngine(forceRebuildChoreo: true);
    }

    void SyncEarCandyToRoomCanvas() {
        if (roomView == null || playerEngine?.SceneHub == null) {
            return;
        }
        playerEngine.SceneHub.CopyEarCandySnapshotZeroAlloc(roomEarCandyScratch, out float sparkle01, out int activeCount);
        roomView.UpdateEarCandyState(roomEarCandyScratch, activeCount, sparkle01, triggerInvalidate: false);
    }

    void StartOffUiSceneAndOnnxWarmupAsync() {
        startupWarmupCts?.Cancel();
        startupWarmupCts?.Dispose();
        startupWarmupCts = new CancellationTokenSource();
        CancellationToken ct = startupWarmupCts.Token;

        _ = Task.Run(async () => {
            try {
                ct.ThrowIfCancellationRequested();
                BinauralProfileService.ApplySavedProfile(this, store);
                ct.ThrowIfCancellationRequested();
                AutoEqService.EnsureLoaded(this);
                ct.ThrowIfCancellationRequested();
                if (neuralRuntime != null) {
                    await neuralRuntime.ProbeCapabilitiesAsync().ConfigureAwait(false);
                }
                RunOnUiThread(() => {
                    if (uiDestroyed || ct.IsCancellationRequested || playerEngine == null) {
                        return;
                    }
                    BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                    AutoEqService.ApplySavedToEngine(this, store, playerEngine);
                    SyncNeuralRuntimeDiagnostics();
                    RefreshBinauralProfileUi();
                    RefreshAutoEqUi();
                    RefreshNeuralAudioUi();
                    RefreshSpatialChoreoClarifierUi();
                });
            } catch (System.OperationCanceledException) {
            } catch (Exception ex) {
                AppLogger.Warn($"StartOffUiSceneAndOnnxWarmupAsync: {ex.Message}");
            }
        }, ct);
    }

    void BindActiveTrackToUiAndPlayerOffUi(CavernizeTrack activeTrack, bool autoPlayAfter) {
        if (activeTrack == null) {
            return;
        }
        string activeSourcePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        if (!string.Equals(activeSourcePath, lastRecordedTrackSourcePath, StringComparison.OrdinalIgnoreCase)) {
            StopStudioPerformanceRecording(autoSelectInExport: false);
            playerEngine?.SetPerformancePreviewTake(null, enabled: false);
            playerEngine?.LiveMicBridge?.ClearRecordedPerformance();
            if (store != null && store.StudioOptPerformance) {
                store.StudioOptPerformance = false;
                if (studioOptPerformanceSwitch != null && studioOptPerformanceSwitch.Checked) {
                    studioOptPerformanceSwitch.Checked = false;
                }
            }
            lastRecordedTrackSourcePath = activeSourcePath;
            RefreshStudioW12ContextUi();
        }

        AppLogger.Info($"Active track ready: {activeTrack.FormatHeader}");
        string details = string.Join(" | ", activeTrack.Details.Select(d => $"{d.property}: {d.value}"));
        SetWorkspaceSummaryText($"{activeTrack.FormatHeader}\n{details}");
        RebuildInlineTrackList();
        SyncTrackLyricsMoodToEngine();

        double pendingSeek = pendingSeekAfterLoadSeconds;
        pendingSeekAfterLoadSeconds = -1;

        if (!(activeTrack.Supported && activeTrack.Codec != Codec.TrueHD)) {
            SetFallbackTrackSourceItems(TrackSourceItem.BuildFromTrack(activeTrack));
            FinalizeTrackUiPostEngineLoad(activeTrack, activeSourcePath, initialSeekSec: 0, autoPlayAfter: false);
            return;
        }

        trackBindCts?.Cancel();
        trackBindCts?.Dispose();
        trackBindCts = new CancellationTokenSource();
        CancellationToken ct = trackBindCts.Token;
        int myGen = Interlocked.Increment(ref trackBindGeneration);

        RecordActiveTrackMeasuredLoudnessIfValid();
        _ = BindTrackOffUiAsync(activeTrack, activeSourcePath, pendingSeek, autoPlayAfter, myGen, ct);
    }

    async Task BindTrackOffUiAsync(
        CavernizeTrack activeTrack,
        string activeSourcePath,
        double pendingSeek,
        bool autoPlayAfter,
        int myGen,
        CancellationToken ct) {
        try {
            await playerEngine.LoadAsync(activeTrack, upmixingSettings, renderingSettings, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || uiDestroyed || myGen != Volatile.Read(ref trackBindGeneration)) {
                return;
            }
            PrimeEngineLoudnessForPath(activeSourcePath, currentMediaMeta);
            double initialSeekSec = 0;
            if (pendingSeek >= 0 && playerEngine.TotalSeconds > 0) {
                initialSeekSec = Math.Clamp(pendingSeek, 0, Math.Max(0, playerEngine.TotalSeconds - 0.1));
                playerEngine.Seek(initialSeekSec);
            }
            RunOnUiThread(() => {
                if (uiDestroyed || ct.IsCancellationRequested || myGen != Volatile.Read(ref trackBindGeneration) ||
                    GetActiveTrack() != activeTrack) {
                    return;
                }
                SetLiveTrackSourceItems();
                if (initialSeekSec > 0) {
                    musicVideoStage?.SeekTo(initialSeekSec);
                }
                FinalizeTrackUiPostEngineLoad(activeTrack, activeSourcePath, initialSeekSec, autoPlayAfter);
            });
        } catch (System.OperationCanceledException) {
        } catch (Exception ex) {
            AppLogger.Error("PlayerEngine.LoadAsync", ex);
            RunOnUiThread(() => {
                if (uiDestroyed || myGen != Volatile.Read(ref trackBindGeneration)) {
                    return;
                }
                SetFallbackTrackSourceItems(TrackSourceItem.BuildFromTrack(activeTrack));
                ShowError(ex.Message);
                FinalizeTrackUiPostEngineLoad(activeTrack, activeSourcePath, initialSeekSec: 0, autoPlayAfter: false);
            });
        }
    }

    void FinalizeTrackUiPostEngineLoad(CavernizeTrack activeTrack, string activeSourcePath, double initialSeekSec, bool autoPlayAfter) {
        string metaTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : "Cavernize 3D");
        string metaArtist = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist)
            ? currentMediaMeta.Artist
            : $"Cavernize 3D • {activeTrack.FormatHeader}";
        string metaAlbum = !string.IsNullOrWhiteSpace(currentMediaMeta?.Album)
            ? currentMediaMeta.Album
            : activeTrack.FormatHeader;

        mediaNotification?.UpdateMetadata(
            metaTitle,
            metaArtist,
            metaAlbum,
            playerEngine?.TotalSeconds ?? 0,
            currentMediaMeta?.ArtworkBitmap);

        RefreshPlayerSourcesList();
        RefreshMusicPlayerUi();
        RefreshExtractorChecklist();
        RefreshConverterTrackInfo();
        RefreshContainerOptions();
        SyncPlaybackUi(false, initialSeekSec, playerEngine?.TotalSeconds ?? 0);
        UpdateRoomCanvas();
        playerEngine?.VocalMixMatchStage?.ApplySongDna(Cavern.VocalChain.VocalMixDnaProfile.CreateStudioDefault());
        StartLosslessTwinDiscoveryAsync(activeTrack, startupDelayMs: 1400);
        TryLoadCachedSongVocalDnaAsync(activeTrack, startupDelayMs: 300);
        TryAutoBindCachedNeuralStemsAsync(activeTrack, startupDelayMs: 450);
        ScheduleNextTrackLoudnessPreScan(activeSourcePath);
        RefreshNeuralPrepUi();
        RefreshSpatialChoreoClarifierUi();
        if (autoPlayAfter && activeTrack.Supported && !(playerEngine?.IsPlaying ?? false)) {
            TogglePlayPause();
        }
    }

    async Task BindSpatialStemsOffUiThreadAsync(
        NeuralSeparatedTrack separatedTrack,
        NeuralPipelineGoal goal,
        CancellationToken ct) {
        if (playerEngine == null) {
            return;
        }
        if (goal == NeuralPipelineGoal.SpatialKaraoke) {
            await Task.Run(() => {
                ct.ThrowIfCancellationRequested();
                playerEngine.BindKaraokeReferenceTrack(separatedTrack);
            }, ct).ConfigureAwait(false);
        } else if (goal == NeuralPipelineGoal.StemSeparation) {
            await playerEngine.BindSpatialUpmixTrackAsync(separatedTrack, ct).ConfigureAwait(false);
        }
    }

    void ReattachPlayerTrackV42() {
        if (playerEngine == null) {
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported || active.Codec == Codec.TrueHD) {
            return;
        }
        bool wasPlaying = playerEngine.IsPlaying;
        double sec = playerEngine.CurrentSeconds;
        trackBindCts?.Cancel();
        trackBindCts?.Dispose();
        trackBindCts = new CancellationTokenSource();
        CancellationToken ct = trackBindCts.Token;
        int myGen = Interlocked.Increment(ref trackBindGeneration);

        _ = Task.Run(async () => {
            try {
                await playerEngine.LoadAsync(active, upmixingSettings, renderingSettings, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || uiDestroyed || myGen != Volatile.Read(ref trackBindGeneration)) {
                    return;
                }
                if (sec > 0) {
                    playerEngine.Seek(sec);
                }
                if (wasPlaying) {
                    playerEngine.IsPlaying = true;
                }
                RunOnUiThread(() => {
                    if (uiDestroyed || ct.IsCancellationRequested || myGen != Volatile.Read(ref trackBindGeneration)) {
                        return;
                    }
                    SetLiveTrackSourceItems();
                    RefreshPlayerSourcesList();
                    RefreshExtractorChecklist();
                    RefreshSpatialChoreoClarifierUi();
                    UpdateRoomCanvas();
                });
            } catch (System.OperationCanceledException) {
            } catch (Exception ex) {
                AppLogger.Warn($"ReattachPlayerTrackV42: {ex.Message}");
            }
        }, ct);
    }

    void CancelV42BackgroundTasks() {
        try { startupWarmupCts?.Cancel(); } catch { }
        startupWarmupCts?.Dispose();
        startupWarmupCts = null;
        try { trackBindCts?.Cancel(); } catch { }
        trackBindCts?.Dispose();
        trackBindCts = null;
        try { choreoBuildCts?.Cancel(); } catch { }
        choreoBuildCts?.Dispose();
        choreoBuildCts = null;
    }
}
