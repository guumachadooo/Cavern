using Cavern.Format.Common;
using System.IO;
using System.Linq;
using System.Text.Json;

using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.VocalChain;
using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
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

    IReadOnlyList<Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo> FindAllActiveSongCacheVariants(
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal) {
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || neuralTrackCache == null) {
            return Array.Empty<Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo>();
        }
        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileNameWithoutExtension(activePath);
        string artist = (currentMediaMeta?.Artist ?? "").Trim();
        return neuralTrackCache.FindEntriesForTrack(activePath, title, artist, goal);
    }

    void ActivateCachedVariantAsync(
        Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo entry,
        Cavernize.Logic.Spatial.NeuralPipelineGoal goal,
        CavernizeTrack activeTrack,
        Action onActivated) {
        if (entry == null || neuralTrackCache == null || playerEngine == null || activeTrack == null) {
            return;
        }
        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileNameWithoutExtension(activePath);
        int sr = activeTrack.SampleRate >= 8000 ? activeTrack.SampleRate : 48000;
        string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
        _ = Task.Run(async () => {
            if (!neuralTrackCache.TryLoad(entry.CacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack loaded) || loaded == null) {
                return;
            }
            if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                EnsureCrossVariantAlignmentForCachedTrack(activeTrack, activePath, title, sr, ffLoc, loaded, CancellationToken.None);
            }
            Cavern.VocalChain.VocalMixDnaProfile dna =
                Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(loaded, title);
            if (dna != null && dna.IsExtractedFromSong) {
                SaveCachedSongVocalDna(activeTrack, dna);
            }
            if (!uiDestroyed && GetActiveTrack() == activeTrack) {
                await BindSpatialStemsOffUiThreadAsync(loaded, goal, CancellationToken.None).ConfigureAwait(false);
            }
            RunOnUiThread(() => {
                if (uiDestroyed || GetActiveTrack() != activeTrack) {
                    return;
                }
                SyncNeuralReferenceOffsetForActiveTrack(activeTrack);
                ApplyExtractedSongDnaOnUiThread(dna);
                SyncNeuralRuntimeDiagnostics();
                NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Playback | UiStateDomain.Upmix, immediate: true);
                Toast.MakeText(this, string.Format(S["AiCacheVariantActivatedToast"], loaded.Stems.Count), ToastLength.Short).Show();
                onActivated?.Invoke();
            });
        });
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
            IReadOnlyList<Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo> variants = FindAllActiveSongCacheVariants(goal);
            (Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo best, Cavernize.Logic.Spatial.NeuralCacheConfigComparison cmp) =
                InspectActiveSongCacheState(goal);

            if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                bool hasReusableVocals =
                    (playerEngine?.NeuralStage?.ActiveSpatialUpmixTrack?.FindStem("vocals") != null) ||
                    (variants != null && variants.Count > 0);
                if (hasReusableVocals) {
                    TextView reuseHint = Label(S["AiCacheIncrementalReuseHint"], 11.2f, OneUiTheme.PrimaryHigh);
                    reuseHint.SetPadding(0, 0, 0, Dp(6));
                    card.AddView(reuseHint);
                }
            }

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

                // Multi-variant non-destructive cache switcher (shows all saved variants for this song)
                if (variants != null && variants.Count > 0) {
                    TextView varHdr = Label(string.Format(S["AiCacheVariantsHeaderFmt"], variants.Count), 11.4f, OneUiTheme.TextSecondary);
                    varHdr.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
                    varHdr.SetPadding(0, Dp(4), 0, Dp(4));
                    card.AddView(varHdr);

                    Cavernize.Logic.Spatial.NeuralSeparatedTrack activeBound =
                        goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke
                            ? playerEngine?.NeuralStage?.ActiveKaraokeTrack
                            : playerEngine?.NeuralStage?.ActiveSpatialUpmixTrack;

                    int visibleCount = Math.Min(variants.Count, 5);
                    for (int vi = 0; vi < visibleCount; vi++) {
                        Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo entry = variants[vi];
                        bool isActiveVariant = activeBound != null &&
                            !string.IsNullOrEmpty(entry.PlanSignature) &&
                            string.Equals(activeBound.PlanSignature, entry.PlanSignature, StringComparison.OrdinalIgnoreCase);

                        LinearLayout row = new(this) { Orientation = Orientation.Vertical };
                        row.SetPadding(Dp(9), Dp(6), Dp(9), Dp(6));
                        row.Background = OneUiTheme.CreateRoundedDrawable(
                            this,
                            isActiveVariant
                                ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 32)
                                : OneUiTheme.WithAlpha(OneUiTheme.SurfaceElevated, 235),
                            12f,
                            isActiveVariant ? OneUiTheme.Primary : OneUiTheme.Divider,
                            isActiveVariant ? 1.2f : 0.8f);

                        string originLabel = !string.IsNullOrWhiteSpace(entry.OriginTag) ? entry.OriginTag : "AI";
                        string fmtLabel = Cavernize.Logic.Spatial.NeuralCacheConfigDiff.ExtractSourceFormatLabel(entry.SourceTrackKey);
                        double entryMb = entry.CompressedBytes / (1024.0 * 1024.0);
                        string rowText = string.Format(
                            S["AiCacheVariantRowFmt"],
                            originLabel,
                            entry.StemCount,
                            entryMb,
                            entry.SavedPercent,
                            fmtLabel);
                        if (isActiveVariant) {
                            rowText = $"[{S["AiCacheVariantActiveBadge"]}] {rowText}";
                        }
                        TextView rowTv = Label(rowText, 11.3f, isActiveVariant ? OneUiTheme.TextPrimary : OneUiTheme.TextSecondary);
                        row.AddView(rowTv);

                        if (!string.IsNullOrWhiteSpace(entry.ModelSummary)) {
                            TextView modelTv = Label(entry.ModelSummary, 10.6f, OneUiTheme.TextSub);
                            modelTv.SetPadding(0, Dp(1), 0, Dp(4));
                            row.AddView(modelTv);
                        }

                        LinearLayout btnBar = new(this) { Orientation = Orientation.Horizontal, BaselineAligned = false };
                        if (!isActiveVariant) {
                            Button useBtn = PillButton(S["AiCacheVariantUseBtn"], () => {
                                ActivateCachedVariantAsync(entry, goal, activeTrack, dismissDialog);
                            }, active: true);
                            useBtn.TextSize = 11.0f;
                            btnBar.AddView(useBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                                RightMargin = Dp(6)
                            });
                        }
                        Button delOneBtn = PillButton(S["AiCacheVariantDeleteBtn"], () => {
                            string keyToDelete = entry.CacheKey;
                            string variantLabel = !string.IsNullOrWhiteSpace(entry.ModelSummary) ? entry.ModelSummary : entry.OriginTag;
                            ConfirmDestructiveAction(
                                S["ConfirmDeleteTitle"],
                                string.Format(S["ConfirmDeleteVariantMsgFmt"], variantLabel),
                                () => {
                                    _ = Task.Run(() => {
                                        neuralTrackCache?.DeleteEntry(keyToDelete);
                                        RunOnUiThread(() => {
                                            if (uiDestroyed) {
                                                return;
                                            }
                                            if (isActiveVariant) {
                                                if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke) {
                                                    playerEngine?.BindKaraokeReferenceTrack(null);
                                                } else {
                                                    playerEngine?.BindSpatialUpmixTrack(null);
                                                }
                                            }
                                            SyncNeuralRuntimeDiagnostics();
                                            NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Playback, immediate: true);
                                            Toast.MakeText(this, S["AiCacheVariantDeletedToast"], ToastLength.Short).Show();
                                            dismissDialog?.Invoke();
                                        });
                                    });
                                });
                        });
                        delOneBtn.TextSize = 11.0f;
                        delOneBtn.SetTextColor(OneUiTheme.FunctionalRed);
                        btnBar.AddView(delOneBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                        row.AddView(btnBar);

                        card.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                }

                string extractBtnLabel = !matches
                    ? S["AiCacheExtractNewVariantBtn"]
                    : S["AiCacheReextractCurrentBtn"];
                Button reextractBtn = PillButton(extractBtnLabel, () => {
                    dismissDialog?.Invoke();
                    PrepareActiveTrackNeuralStemsAsync(goal, silentIfNotReady: false, forceReextract: true);
                }, active: !matches);
                reextractBtn.TextSize = 11.8f;
                card.AddView(reextractBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(2)
                });

                Button compactBtn = PillButton(S["AiCacheOptimizeStorageBtn"], () => {
                    _ = Task.Run(() => {
                        (int compactedCount, long reclaimedBytes) = neuralTrackCache != null
                            ? neuralTrackCache.OptimizeCacheStorage()
                            : (0, 0L);
                        RunOnUiThread(() => {
                            if (uiDestroyed) {
                                return;
                            }
                            SyncNeuralRuntimeDiagnostics();
                            string msg = compactedCount > 0 && reclaimedBytes > 0L
                                ? string.Format(S["AiCacheOptimizeDoneToastFmt"], compactedCount, reclaimedBytes / (1024.0 * 1024.0))
                                : S["AiCacheOptimizeAlreadyCompactToast"];
                            Toast.MakeText(this, msg, ToastLength.Short).Show();
                        });
                    });
                });
                compactBtn.TextSize = 11.4f;
                card.AddView(compactBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(6)
                });

                string removeLabel = variants != null && variants.Count > 1
                    ? string.Format(S["AiCacheRemoveAllVariantsBtn"], variants.Count)
                    : S["AiCacheRemoveBtn"];
                Button removeBtn = PillButton(removeLabel, () => {
                    ConfirmDestructiveAction(
                        S["ConfirmDeleteTitle"],
                        S["ConfirmRemoveAllVariantsMsg"],
                        () => {
                            string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
                            string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                                ? currentMediaMeta.Title
                                : Path.GetFileNameWithoutExtension(activePath);
                            string artist = (currentMediaMeta?.Artist ?? "").Trim();
                            neuralTrackCache?.DeleteEntriesForTrack(activePath, title, artist);
                            playerEngine?.NeuralStage?.ClearAllSeparatedTracks();
                            playerEngine?.BindSeparatedNeuralTrack(null);
                            SyncNeuralRuntimeDiagnostics();
                            NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.Playback, immediate: true);
                            Toast.MakeText(this, S["AiCacheRemovedToast"], ToastLength.Short).Show();
                            dismissDialog?.Invoke();
                        });
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

        if (ShouldHideChannelUpmixSelectors()) {
            string reason = IsActiveTrackMultichannelOrAtmos() ? S["UpmixHiddenAtmosHint"] : S["UpmixHiddenAi3DHint"];
            TextView hiddenHint = Label(reason, 12.0f, OneUiTheme.PeachText);
            hiddenHint.SetPadding(0, 0, 0, Dp(6));
            content.AddView(hiddenHint);
        } else {
            OneUiInlineSelector sel = new(this, S["UpmixModeLabel"]);
            sel.SetItems([S["UpmixModeDirect"], S["UpmixModeSurround"], S["UpmixModeFull3D"]], ResolveUpmixModeIndex());
            sel.SelectionChanged += pos => SetClassicUpmixMode(pos);
            content.AddView(WrapControlWithHelp(sel, S["UpmixModeLabel"].TrimEnd(':'), S["TipUpmixMode"], bottomMarginDp: 6));
        }

        AddDivider(content);
        AddSwitchRow(content, S["AtmosAwareUpmix"], store.AtmosAwareUpmix, SetAtmosAwareUpmix, helpBody: S["TipAtmosAwareUpmix"]);

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
}
