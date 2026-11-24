using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Spatial;

using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Audio;
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
    readonly List<Button> studioTierPillBtns = [];
    LinearLayout studioCodecDetailsHeader, studioCodecDetailsBody, studioAtmosLockBanner;
    OneUiIconView studioCodecDetailsChevron;
    TextView studioCodecDetailsSummaryText, studioCodecDetailsBodyText;
    Button studioSwitchEclipsaBtn;
    bool isStudioCodecDetailsExpanded;
    Switch studioOptAiUpmixSwitch, studioOptKaraokeSwitch, studioOptRemoveBackingSwitch, studioOptPerformanceSwitch;
    Switch studioOptLosslessTwinSwitch, studioOptHeadphonePersonalSwitch;
    Switch studioOptEmbedCoverSwitch, studioOptEmbedLyricsSwitch;
    OneUiInlineSelector studioBundleSelector, studioDynamicMotionSelector, studioExportFolderSelector;
    TextView studioOptPerformanceHintText, studioOptTwinHintText;
    TextView studioOptEmbedCoverHintText, studioOptEmbedLyricsHintText, studioExportFolderHintText;
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

        // One UI 9 3-pill StudioCompressionTier selector (Master PCM | Lossless | Portable Lossy)
        LinearLayout tierHdrRow = HRow();
        tierHdrRow.SetPadding(0, Dp(2), 0, Dp(5));
        tierHdrRow.AddView(Label(S["StudioCompressionTierLabel"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        tierHdrRow.AddView(CreateInlineHelpBtn(S["StudioCompressionTierLabel"], S["TipStudioCompressionTier"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        card.AddView(tierHdrRow);

        LinearLayout tierPillRow = HRow();
        tierPillRow.SetPadding(0, 0, 0, Dp(6));
        studioTierPillBtns.Clear();
        string[] tierLabels = [
            S["StudioTierMasterPcm"],
            S["StudioTierLossless"],
            S["StudioTierPortable"]
        ];
        int savedTierIdx = Math.Clamp(store?.StudioCompressionTierIndex ?? 0, 0, 2);
        for (int i = 0; i < tierLabels.Length; i++) {
            int tierIdx = i;
            Button pill = PillButton(tierLabels[i], () => {
                if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
                    return;
                }
                if (store != null) {
                    store.StudioCompressionTierIndex = tierIdx;
                }
                RefreshStudioW12ContextUi();
            }, active: i == savedTierIdx);
            pill.TextSize = 11.2f;
            pill.SetPadding(Dp(6), Dp(7), Dp(6), Dp(7));
            studioTierPillBtns.Add(pill);
            tierPillRow.AddView(pill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                RightMargin = i < tierLabels.Length - 1 ? Dp(6) : 0
            });
        }
        card.AddView(tierPillRow);

        // Collapsed Codec Details summary line (GetCompressionSpecSummary + estimated extension)
        studioCodecDetailsHeader = HRow();
        studioCodecDetailsHeader.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        studioCodecDetailsHeader.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceSubTile, 12f, OneUiTheme.BorderSubtle, 0.8f);
        OneUiTheme.AttachRecoil(studioCodecDetailsHeader, 0.98f);
        studioCodecDetailsSummaryText = Label("", 11.2f, OneUiTheme.TextSecondary);
        studioCodecDetailsSummaryText.SetSingleLine(true);
        studioCodecDetailsSummaryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        studioCodecDetailsChevron = new OneUiIconView(
            this,
            isStudioCodecDetailsExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown,
            OneUiTheme.TextSub);
        studioCodecDetailsHeader.AddView(studioCodecDetailsSummaryText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
            RightMargin = Dp(6)
        });
        studioCodecDetailsHeader.AddView(studioCodecDetailsChevron, new LinearLayout.LayoutParams(Dp(14), Dp(14)));
        card.AddView(studioCodecDetailsHeader, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        studioCodecDetailsBody = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isStudioCodecDetailsExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        studioCodecDetailsBody.SetPadding(Dp(10), Dp(4), Dp(10), Dp(6));
        studioCodecDetailsBodyText = Label("", 11f, OneUiTheme.TextSub);
        studioCodecDetailsBody.AddView(studioCodecDetailsBodyText);
        card.AddView(studioCodecDetailsBody);

        studioCodecDetailsHeader.Click += (_, _) => {
            isStudioCodecDetailsExpanded = !isStudioCodecDetailsExpanded;
            SetExpandedWithAnimation(studioCodecDetailsBody, isStudioCodecDetailsExpanded);
            studioCodecDetailsChevron.IconType = isStudioCodecDetailsExpanded
                ? OneUiIconType.ChevronUp
                : OneUiIconType.ChevronDown;
        };

        // Inline DolbyAtmosAdm Tier 2/3 lock warning banner with 1-tap "Switch to Eclipsa IAMF" button
        studioAtmosLockBanner = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        studioAtmosLockBanner.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        studioAtmosLockBanner.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 28),
            12f,
            OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 140),
            1f);
        TextView lockWarnTv = Label(S["StudioAtmosTierLockWarn"], 11.2f, OneUiTheme.PeachText);
        lockWarnTv.SetPadding(0, 0, 0, Dp(6));
        studioAtmosLockBanner.AddView(lockWarnTv);
        studioSwitchEclipsaBtn = PillButton(S["StudioSwitchToEclipsaBtn"], () => {
            if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
                return;
            }
            int eclipsaIdx = (int)StudioBaseOutputFormat.EclipsaIamf;
            if (store != null) {
                store.StudioBaseFormatIndex = eclipsaIdx;
            }
            studioBaseFormatSelector?.SetSelection(eclipsaIdx, notify: false);
            RefreshStudioW12ContextUi();
        }, active: true);
        studioSwitchEclipsaBtn.TextSize = 11.4f;
        studioAtmosLockBanner.AddView(studioSwitchEclipsaBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        card.AddView(studioAtmosLockBanner, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

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

        studioBundleSelector = new OneUiInlineSelector(this, S["NeuralStudioBundleTitle"]);
        studioBundleSelector.SetItems(new[] {
            S["StudioBundleFastSingle"],
            S["StudioBundleVocalEnsemble"],
            S["StudioBundlePureInstrumental"],
            S["StudioBundleFullBand6Stem"],
            S["StudioBundleKaraoke2Stage"]
        }, Math.Clamp(store.NeuralStudioBundleIndex, 0, 4));
        studioBundleSelector.SelectionChanged += idx => {
            store.NeuralStudioBundleIndex = idx;
            ApplyAndRefreshNeuralAudio();
            RefreshStudioW12ContextUi();
        };
        card.AddView(studioBundleSelector, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

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
        studioOptRemoveBackingSwitch = AddExplainedSwitchRow(
            studioW12KaraokeContainer,
            S["StudioOptRemoveBackingTitle"],
            S["StudioOptRemoveBackingDesc"],
            !store.KaraokePreserveBackingVocals,
            removeAll => {
                store.KaraokePreserveBackingVocals = !removeAll;
                ApplyAndRefreshNeuralAudio();
                RefreshLyricsSingDockUi();
                RefreshStudioW12ContextUi();
            },
            out _);
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

        // Master Overhaul T5: Explicit Studio Dynamic Atmos Object Choreography selector (independent of 3D Room playback)
        AddDivider(card);
        LinearLayout motionHdrRow = HRow();
        motionHdrRow.SetPadding(0, Dp(2), 0, Dp(4));
        motionHdrRow.AddView(Label(S["StudioDynamicMotionLabel"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        motionHdrRow.AddView(CreateInlineHelpBtn(S["StudioDynamicMotionLabel"], S["TipStudioDynamicMotion"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        card.AddView(motionHdrRow);

        studioDynamicMotionSelector = new OneUiInlineSelector(this, S["StudioDynamicMotionLabel"]);
        studioDynamicMotionSelector.SetItems(new[] {
            S["SpatialChoreoOff"],
            S["SpatialChoreoSubtle"],
            S["SpatialChoreoMusical"],
            S["SpatialChoreoCinematic"],
            S["SpatialChoreoOrbit"]
        }, Math.Clamp(store?.StudioDynamicChoreoModeIndex ?? 0, 0, 4));
        studioDynamicMotionSelector.SelectionChanged += idx => {
            if (store != null) {
                store.StudioDynamicChoreoModeIndex = idx;
            }
            RefreshStudioW12ContextUi();
        };
        card.AddView(studioDynamicMotionSelector, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // Master Overhaul T5: Metadata Preservation, Optional Cover Art & Synced Lyrics, and Public Storage Folder
        AddDivider(card);
        TextView deliveryHeader = Label(S["StudioExportDeliveryHeader"], 12.2f, OneUiTheme.TextPrimary);
        deliveryHeader.SetTypeface(null, TypefaceStyle.Bold);
        deliveryHeader.SetPadding(0, Dp(2), 0, Dp(4));
        card.AddView(deliveryHeader);

        studioOptEmbedCoverSwitch = AddExplainedSwitchRow(
            card,
            S["StudioOptEmbedCoverTitle"],
            S["StudioOptEmbedCoverDesc"],
            store?.StudioExportEmbedCoverArt ?? true,
            on => {
                if (store != null) {
                    store.StudioExportEmbedCoverArt = on;
                }
                RefreshStudioW12ContextUi();
            },
            out studioOptEmbedCoverHintText);

        studioOptEmbedLyricsSwitch = AddExplainedSwitchRow(
            card,
            S["StudioOptEmbedLyricsTitle"],
            S["StudioOptEmbedLyricsDesc"],
            store?.StudioExportEmbedLyrics ?? true,
            on => {
                if (store != null) {
                    store.StudioExportEmbedLyrics = on;
                }
                RefreshStudioW12ContextUi();
            },
            out studioOptEmbedLyricsHintText);

        LinearLayout folderHdrRow = HRow();
        folderHdrRow.SetPadding(0, Dp(4), 0, Dp(4));
        folderHdrRow.AddView(Label(S["StudioExportFolderLabel"], 12f, ColMuted), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        folderHdrRow.AddView(CreateInlineHelpBtn(S["StudioExportFolderLabel"], S["TipStudioExportFolder"]), new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        card.AddView(folderHdrRow);

        studioExportFolderSelector = new OneUiInlineSelector(this, S["StudioExportFolderLabel"]);
        studioExportFolderSelector.SetItems(new[] {
            S["StudioExportFolderMusic"],
            S["StudioExportFolderDownloads"]
        }, Math.Clamp(store?.StudioExportFolderIndex ?? 0, 0, 1));
        studioExportFolderSelector.SelectionChanged += idx => {
            if (store != null) {
                store.StudioExportFolderIndex = idx;
            }
            RefreshStudioW12ContextUi();
        };
        card.AddView(studioExportFolderSelector);

        studioExportFolderHintText = Label("", 11.1f, OneUiTheme.TextSub);
        studioExportFolderHintText.SetPadding(0, Dp(4), 0, Dp(6));
        card.AddView(studioExportFolderHintText);

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

    string ResolveExportFfmpegPath() {
        string resolved = FFmpegInstallerService.ResolveExistingFFmpegPath(this, store);
        if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) {
            return resolved;
        }
        string loc = ffmpeg?.Location;
        return !string.IsNullOrEmpty(loc) && File.Exists(loc) ? loc : string.Empty;
    }

    ExportStorageDestination GetSelectedExportDestination() =>
        (store?.StudioExportFolderIndex ?? 0) == 1
            ? ExportStorageDestination.DownloadsCavernize
            : ExportStorageDestination.MusicCavernize;

    SpatialChoreoMode GetSelectedStudioDynamicChoreoMode() =>
        (SpatialChoreoMode)Math.Clamp(store?.StudioDynamicChoreoModeIndex ?? 0, 0, 4);

    string GetStudioDynamicChoreoLabel(SpatialChoreoMode mode) => mode switch {
        SpatialChoreoMode.Subtle => S["SpatialChoreoSubtle"],
        SpatialChoreoMode.Musical => S["SpatialChoreoMusical"],
        SpatialChoreoMode.Cinematic => S["SpatialChoreoCinematic"],
        SpatialChoreoMode.Orbit => S["SpatialChoreoOrbit"],
        _ => S["SpatialChoreoOff"]
    };

    SpatialChoreoTrack ResolveActiveChoreoTrackForExport(bool buildIfMissing = false) {
        SpatialChoreoMode mode = GetSelectedStudioDynamicChoreoMode();
        if (mode == SpatialChoreoMode.Off) {
            return null;
        }
        SpatialChoreoTrack existing = playerEngine?.SceneHub?.ActiveChoreoTrack;
        if (existing != null && existing.Mode == mode) {
            return existing;
        }
        NeuralSeparatedTrack sep = playerEngine?.NeuralStage?.ActiveSpatialUpmixTrack;
        if (sep?.Stems == null || sep.Stems.Count == 0 || sep.IsProgressiveStreaming) {
            return null;
        }
        if (neuralTrackCache != null && neuralTrackCache.TryGetAttachedChoreoTrack(sep, out SpatialChoreoTrack cached) &&
            cached != null && cached.Mode == mode) {
            return cached;
        }
        if (!buildIfMissing) {
            return null;
        }
        LyricMoodDigest[] moodDigests = currentMediaMeta?.Lyrics != null
            ? Services.LyricsService.ExtractMoodDigest(currentMediaMeta.Lyrics)
            : null;
        int sr = sep.SampleRate >= 8000 ? sep.SampleRate : (playerEngine?.OutputSampleRate >= 8000 ? playerEngine.OutputSampleRate : 48000);
        SpatialChoreoTrack built = Cavern.Neural.NeuralStemSpatializer.BuildAndSetChoreoTrack(sep.Stems, sr, mode, moodDigests);
        if (built != null) {
            neuralTrackCache?.AttachChoreoTrack(sep, built);
        }
        return built;
    }

    ExportMetadataPackage BuildExportMetadataPackageForActiveTrack(CavernizeTrack active) {
        MediaArtworkMetadata meta = currentMediaMeta;
        string sourcePath = meta?.SourceMediaPath ?? LoadedFile?.Path ?? active?.OriginalSourcePath ?? active?.Path ?? "";
        ParsedAudioTags fileTags = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath)
            ? AudioTagReader.TryReadTags(sourcePath)
            : null;

        string title = !string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title : fileTags?.Title;
        string artist = !string.IsNullOrWhiteSpace(meta?.Artist) ? meta.Artist : fileTags?.Artist;
        string albumArtist = !string.IsNullOrWhiteSpace(meta?.AlbumArtist) ? meta.AlbumArtist : fileTags?.AlbumArtist;
        string album = !string.IsNullOrWhiteSpace(meta?.Album) ? meta.Album : fileTags?.Album;
        int trackNum = meta != null && meta.TrackNumber > 0 ? meta.TrackNumber : (fileTags?.TrackNumber ?? 0);
        int trackTotal = meta != null && meta.TrackTotal > 0 ? meta.TrackTotal : (fileTags?.TrackTotal ?? 0);
        int discNum = meta != null && meta.DiscNumber > 0 ? meta.DiscNumber : (fileTags?.DiscNumber ?? 0);
        string year = !string.IsNullOrWhiteSpace(meta?.Year) ? meta.Year : fileTags?.Year;
        string genre = !string.IsNullOrWhiteSpace(meta?.Genre) ? meta.Genre : fileTags?.Genre;
        string comment = !string.IsNullOrWhiteSpace(meta?.Comment) ? meta.Comment : fileTags?.Comment;

        if (string.IsNullOrWhiteSpace(title)) {
            string fallbackStem = Path.GetFileNameWithoutExtension(sourcePath);
            title = ExportFileNameFormatter.BuildCleanTitleStem(null, fallbackStem);
        }

        bool embedCover = store?.StudioExportEmbedCoverArt ?? true;
        byte[] coverBytes = null;
        int coverW = 0;
        int coverH = 0;
        if (embedCover) {
            coverBytes = MediaMetadataHelper.TryExtractCoverArtJpegBytes(
                this,
                meta,
                currentMediaUri,
                sourcePath,
                out coverW,
                out coverH);
        }

        bool embedLyrics = store?.StudioExportEmbedLyrics ?? true;
        string syncedLrc = "";
        string plainLyrics = "";
        if (embedLyrics && meta?.Lyrics != null && meta.Lyrics.HasLyrics) {
            string serialized = Services.LyricsService.SerializeToLrc(meta.Lyrics);
            if (!string.IsNullOrWhiteSpace(serialized)) {
                if (meta.Lyrics.IsSynced) {
                    syncedLrc = serialized;
                } else {
                    plainLyrics = serialized;
                }
            }
        }

        return new ExportMetadataPackage {
            Title = title ?? "",
            Artist = artist ?? "",
            AlbumArtist = albumArtist ?? "",
            Album = album ?? "",
            TrackNumber = trackNum,
            TrackTotal = trackTotal,
            DiscNumber = discNum,
            Year = year ?? "",
            Genre = genre ?? "",
            Comment = comment ?? "",
            Encoder = "Cavernize",
            EmbedCoverArt = embedCover && coverBytes != null && coverBytes.Length > 0,
            CoverArtBytes = coverBytes,
            CoverArtMimeType = "image/jpeg",
            CoverArtWidth = coverW,
            CoverArtHeight = coverH,
            EmbedLyrics = embedLyrics && (!string.IsNullOrWhiteSpace(syncedLrc) || !string.IsNullOrWhiteSpace(plainLyrics)),
            SyncedLyricsLrc = syncedLrc,
            PlainLyrics = plainLyrics
        };
    }

    StudioComposableExportOptions BuildCurrentComposableOptions(bool buildChoreoIfMissing = false) {
        int fmtIdx = Math.Clamp(studioBaseFormatSelector?.SelectedIndex ?? store?.StudioBaseFormatIndex ?? 0, 0, 4);
        StudioCompressionTier tier = (StudioCompressionTier)Math.Clamp(store?.StudioCompressionTierIndex ?? 0, 0, 2);
        SpatialChoreoMode dynamicMode = GetSelectedStudioDynamicChoreoMode();
        LyricMoodDigest[] moodDigests = dynamicMode != SpatialChoreoMode.Off && currentMediaMeta?.Lyrics != null
            ? Services.LyricsService.ExtractMoodDigest(currentMediaMeta.Lyrics)
            : null;
        PerformanceRecordingTake take = playerEngine?.LiveMicBridge?.LatestRecordedTake;
        bool includePerf = store?.StudioOptPerformance ?? false;
        return new StudioComposableExportOptions {
            BaseFormat = (StudioBaseOutputFormat)fmtIdx,
            CompressionTier = tier,
            DynamicChoreoMode = dynamicMode,
            LyricMoodDigests = moodDigests,
            FfmpegExecutablePath = ResolveExportFfmpegPath(),
            ChoreoTrack = ResolveActiveChoreoTrackForExport(buildChoreoIfMissing),
            OnSigpipeShield = () => { NativeProAudioBridge.InstallSigpipeShield(); },
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
        StudioCompressionTier tier = opts.CompressionTier;
        int tierIdx = Math.Clamp((int)tier, 0, 2);

        if (studioBaseFormatDescText != null) {
            studioBaseFormatDescText.Text = GetStudioBaseFormatDescription(opts.BaseFormat);
        }
        for (int i = 0; i < studioTierPillBtns.Count; i++) {
            bool sel = i == tierIdx;
            Button pill = studioTierPillBtns[i];
            pill.Background = sel
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            pill.SetTypeface(null, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        string effectiveExt = StudioBatchExporter.GetEffectiveOutputExtension(opts.BaseFormat, tier, true);
        string specSummary = StudioBatchExporter.GetCompressionSpecSummary(opts.BaseFormat, tier);
        if (studioCodecDetailsSummaryText != null) {
            studioCodecDetailsSummaryText.Text = string.Format(S["StudioCodecDetailsToggleFmt"], specSummary, effectiveExt);
        }
        if (studioCodecDetailsBodyText != null) {
            studioCodecDetailsBodyText.Text = string.Format(
                S["StudioCodecDetailsBodyFmt"],
                effectiveExt,
                specSummary,
                store.ResolveTruePeakCeilingDbtp());
        }
        if (studioAtmosLockBanner != null) {
            studioAtmosLockBanner.Visibility = StudioBatchExporter.IsAdmCompressionLocked(opts.BaseFormat, tier)
                ? ViewStates.Visible
                : ViewStates.Gone;
        }
        if (studioW12KaraokeContainer != null) {
            studioW12KaraokeContainer.Visibility = opts.EnableKaraokeInstrumental ? ViewStates.Visible : ViewStates.Gone;
        }
        int vocPct = Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100);
        if (studioW12KaraokeBar != null && studioW12KaraokeBar.Progress != vocPct) {
            studioW12KaraokeBar.Progress = vocPct;
            studioW12KaraokeBar.Invalidate();
        }
        bool removeBacking = !store.KaraokePreserveBackingVocals;
        if (studioOptRemoveBackingSwitch != null && studioOptRemoveBackingSwitch.Checked != removeBacking) {
            studioOptRemoveBackingSwitch.Checked = removeBacking;
        }
        if (studioBundleSelector != null) {
            int bIdx = Math.Clamp(store.NeuralStudioBundleIndex, 0, 4);
            if (studioBundleSelector.SelectedIndex != bIdx) {
                studioBundleSelector.SetSelection(bIdx, notify: false);
            }
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

        if (studioDynamicMotionSelector != null) {
            int mIdx = Math.Clamp(store.StudioDynamicChoreoModeIndex, 0, 4);
            if (studioDynamicMotionSelector.SelectedIndex != mIdx) {
                studioDynamicMotionSelector.SetSelection(mIdx, notify: false);
            }
        }
        if (studioOptEmbedCoverSwitch != null && studioOptEmbedCoverSwitch.Checked != store.StudioExportEmbedCoverArt) {
            studioOptEmbedCoverSwitch.Checked = store.StudioExportEmbedCoverArt;
        }
        if (studioOptEmbedLyricsSwitch != null && studioOptEmbedLyricsSwitch.Checked != store.StudioExportEmbedLyrics) {
            studioOptEmbedLyricsSwitch.Checked = store.StudioExportEmbedLyrics;
        }
        if (studioExportFolderSelector != null) {
            int fIdx = Math.Clamp(store.StudioExportFolderIndex, 0, 1);
            if (studioExportFolderSelector.SelectedIndex != fIdx) {
                studioExportFolderSelector.SetSelection(fIdx, notify: false);
            }
        }

        if (studioOptEmbedCoverHintText != null) {
            bool hasCover = currentMediaMeta != null && currentMediaMeta.HasEmbeddedArtwork;
            if (store.StudioExportEmbedCoverArt && hasCover) {
                studioOptEmbedCoverHintText.Text = S["StudioOptEmbedCoverReadyDesc"];
                studioOptEmbedCoverHintText.SetTextColor(OneUiTheme.FunctionalGreen);
            } else {
                studioOptEmbedCoverHintText.Text = S["StudioOptEmbedCoverDesc"];
                studioOptEmbedCoverHintText.SetTextColor(OneUiTheme.TextSub);
            }
        }

        if (studioOptEmbedLyricsHintText != null) {
            bool hasLyrics = currentMediaMeta?.Lyrics != null && currentMediaMeta.Lyrics.HasLyrics;
            if (store.StudioExportEmbedLyrics && hasLyrics) {
                studioOptEmbedLyricsHintText.Text = currentMediaMeta.Lyrics.IsSynced
                    ? S["StudioOptEmbedLyricsSyncedReadyDesc"]
                    : S["StudioOptEmbedLyricsPlainReadyDesc"];
                studioOptEmbedLyricsHintText.SetTextColor(OneUiTheme.FunctionalGreen);
            } else {
                studioOptEmbedLyricsHintText.Text = S["StudioOptEmbedLyricsDesc"];
                studioOptEmbedLyricsHintText.SetTextColor(OneUiTheme.TextSub);
            }
        }

        if (studioExportFolderHintText != null) {
            ExportStorageDestination dest = GetSelectedExportDestination();
            if (dest == ExportStorageDestination.MusicCavernize &&
                !AndroidFileHelper.IsStandardAndroidMusicExtension(effectiveExt)) {
                studioExportFolderHintText.Text = string.Format(S["StudioExportFolderCompatNoteFmt"], effectiveExt);
                studioExportFolderHintText.SetTextColor(OneUiTheme.PeachText);
            } else {
                string folderDisplay = dest == ExportStorageDestination.DownloadsCavernize
                    ? S["StudioExportFolderDownloads"]
                    : S["StudioExportFolderMusic"];
                studioExportFolderHintText.Text = string.Format(S["StudioExportFolderActiveFmt"], folderDisplay);
                studioExportFolderHintText.SetTextColor(OneUiTheme.TextSub);
            }
        }

        if (studioW12ContextText != null) {
            string tierTag = tier switch {
                StudioCompressionTier.LosslessCompressed => S["StudioTierLossless"],
                StudioCompressionTier.PortableLossy => S["StudioTierPortable"],
                _ => S["StudioTierMasterPcm"]
            };
            List<string> tags = [$"{GetStudioBaseFormatLabel(opts.BaseFormat)} ({tierTag})"];
            if (opts.EnableAiSpatialUpmix) {
                tags.Add(IsActiveTrackMultichannelOrAtmos() ? S["StudioTagAtmosAi"] : S["StudioTagAiUpmix"]);
            }
            if (opts.DynamicChoreoMode != SpatialChoreoMode.Off) {
                tags.Add(string.Format(S["StudioTagMotionFmt"], GetStudioDynamicChoreoLabel(opts.DynamicChoreoMode)));
            }
            if (opts.EnableKaraokeInstrumental) {
                tags.Add(removeBacking
                    ? string.Format(S["StudioTagPureInstrFmt"], vocPct)
                    : string.Format(S["StudioTagKaraokeFmt"], vocPct));
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

    StudioBatchExportJob BuildComposableExportJob(
        StudioComposableExportOptions opts,
        string dir,
        string fallbackSourceName,
        ExportMetadataPackage metadata = null) {
        string ext = StudioBatchExporter.GetEffectiveOutputExtension(opts.BaseFormat, opts.CompressionTier, true);
        string cleanFileName = ExportFileNameFormatter.FormatStudioFileName(
            metadata,
            fallbackSourceName,
            opts.BaseFormat,
            opts.CompressionTier,
            opts.EnableAiSpatialUpmix,
            opts.EnableKaraokeInstrumental,
            !(store?.KaraokePreserveBackingVocals ?? true),
            opts.IncludeRecordedPerformance,
            opts.EnableLosslessTwinRestoration,
            opts.DynamicChoreoMode,
            ext);
        string outPath = ExportFileNameFormatter.ResolveUniqueOutputPath(dir, cleanFileName);

        PerformanceRecordingTake take = playerEngine?.LiveMicBridge?.LatestRecordedTake;
        ExportMetadataPackage clonedMeta = metadata?.Clone();
        opts.Metadata = clonedMeta;
        return new StudioBatchExportJob {
            TrackName = !string.IsNullOrWhiteSpace(clonedMeta?.Title) ? clonedMeta.Title : fallbackSourceName,
            Preset = opts.BaseFormat switch {
                StudioBaseOutputFormat.EclipsaIamf => StudioExportPreset.EclipsaAudioIamf,
                StudioBaseOutputFormat.Ambisonics360 => StudioExportPreset.AmbisonicsHoa3rdOrder,
                StudioBaseOutputFormat.DolbyAtmosAdm => StudioExportPreset.AdmBwfItuBs2076,
                StudioBaseOutputFormat.Cinema714 => StudioExportPreset.HybridNeuralDirAc714,
                _ => StudioExportPreset.PersonalizedLosslessBinaural
            },
            ComposableOptions = opts,
            CompressionTier = opts.CompressionTier,
            DynamicChoreoMode = opts.DynamicChoreoMode,
            LyricMoodDigests = opts.LyricMoodDigests,
            Metadata = clonedMeta,
            FfmpegExecutablePath = opts.FfmpegExecutablePath ?? ResolveExportFfmpegPath(),
            ChoreoTrack = opts.ChoreoTrack,
            OnSigpipeShield = opts.OnSigpipeShield ?? (() => { NativeProAudioBridge.InstallSigpipeShield(); }),
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

    long EstimateStudioExportBytes(CavernizeTrack active, StudioComposableExportOptions opts) {
        int sr = active != null && active.SampleRate > 0 ? active.SampleRate : 48000;
        double durSec = active != null && active.Length > 0 && sr > 0
            ? active.Length / (double)sr
            : Math.Max(30.0, playerEngine?.TotalSeconds ?? 180.0);
        int channels = opts.BaseFormat switch {
            StudioBaseOutputFormat.Headphone3D => 2,
            StudioBaseOutputFormat.Cinema714 => 12,
            StudioBaseOutputFormat.DolbyAtmosAdm => Math.Max(12, active?.Renderer?.Objects?.Count ?? 12),
            StudioBaseOutputFormat.EclipsaIamf => 16,
            StudioBaseOutputFormat.Ambisonics360 => 16,
            _ => 2
        };
        int bytesPerSample = (store?.Force24Bit ?? false) ? 3 : 2;
        double bytesPerSec = opts.CompressionTier switch {
            StudioCompressionTier.PortableLossy => 96000.0,
            StudioCompressionTier.LosslessCompressed => sr * channels * bytesPerSample * 0.65,
            _ => sr * channels * bytesPerSample
        };
        return Math.Max(4L * 1024L * 1024L, (long)Math.Ceiling(durSec * bytesPerSec * 1.15));
    }

    void StartStudioComposableExport() {
        if (isOperationRunning || isPreparingTrueHD || ExportMediaProcessingService.IsExportActive) {
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

        StudioComposableExportOptions opts = BuildCurrentComposableOptions(buildChoreoIfMissing: true);
        if (StudioBatchExporter.IsAdmCompressionLocked(opts.BaseFormat, opts.CompressionTier)) {
            ShowError(S["StudioAtmosTierLockWarn"]);
            return;
        }
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

        ExportStorageDestination dest = GetSelectedExportDestination();
        string ext = StudioBatchExporter.GetEffectiveOutputExtension(opts.BaseFormat, opts.CompressionTier, true);
        string dir = AndroidFileHelper.GetExportWorkingDir(this, dest, ext, out _, out _);
        if (!ExportMediaProcessingService.CheckFreeSpacePreflight(dir, EstimateStudioExportBytes(active, opts), out string spaceErr)) {
            ShowError(string.IsNullOrWhiteSpace(spaceErr) ? S["StudioExportLowSpaceError"] : spaceErr);
            return;
        }

        playerEngine?.SetPerformancePreviewTake(null, enabled: false);
        StopPlayback();
        string baseName = Path.GetFileNameWithoutExtension(currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? "master");
        string label = GetStudioBaseFormatLabel(opts.BaseFormat);
        var appCtx = ApplicationContext ?? this;

        isOperationRunning = true;
        RunOnUiThread(() => {
            HideLastExportActionBar();
            SetRunningUiState(true);
            SetConvertStatus(S["Starting"], 0);
        });

        _ = Task.Run(async () => {
            AndroidRuntimeTelemetry.ApplyBackgroundThreadQoS();
            StudioBatchExportResult res = null;
            StudioBatchExportJob job = null;
            ExportPublishResult publishResult = null;
            bool cancelled = false;
            Exception fatalEx = null;
            try {
                ExportMetadataPackage metadata = BuildExportMetadataPackageForActiveTrack(active);
                job = BuildComposableExportJob(opts, dir, baseName, metadata);
                res = await ExportMediaProcessingService.EnqueueSingleExportAsync(
                    appCtx,
                    active,
                    job,
                    p => {
                        if (!uiDestroyed) {
                            RunOnUiThread(() => SetConvertStatus(label, p));
                        }
                    },
                    label,
                    CancellationToken.None).ConfigureAwait(false);
                if (res == null || res.IsCancelled) {
                    cancelled = true;
                }
            } catch (System.OperationCanceledException) {
                cancelled = true;
            } catch (Exception ex) {
                fatalEx = ex;
                AppLogger.Error("StartStudioComposableExport", ex);
            } finally {
                bool ok = !cancelled && fatalEx == null && res != null && res.Succeeded && !string.IsNullOrEmpty(res.OutputPath) && File.Exists(res.OutputPath);
                string finalPath = res?.OutputPath ?? job?.OutputPath;
                try {
                    await ExportMediaProcessingService.CommitOrCleanupPendingMediaStoreAsync(appCtx, null, finalPath, ok).ConfigureAwait(false);
                    if (ok) {
                        publishResult = AndroidFileHelper.PublishExportedAudio(appCtx, finalPath, dest);
                        if (publishResult != null && !string.IsNullOrEmpty(publishResult.LocalPath)) {
                            finalPath = publishResult.LocalPath;
                        }
                    }
                } catch (Exception cleanupEx) {
                    AppLogger.Warn($"PublishExportedAudio: {cleanupEx.Message}");
                }

                isOperationRunning = false;
                if (!uiDestroyed) {
                    RunOnUiThread(() => {
                        if (uiDestroyed) {
                            return;
                        }
                        if (cancelled) {
                            SetConvertStatus(S["CancelledStatus"], 0);
                            Toast.MakeText(this, S["StudioExportFgCancelledToast"], ToastLength.Short).Show();
                        } else if (fatalEx != null) {
                            SetConvertStatus(fatalEx.Message, 0);
                            ShowError(fatalEx.Message);
                        } else if (!ok) {
                            string errMsg = !string.IsNullOrWhiteSpace(res?.ErrorMessage) ? res.ErrorMessage : S["RenderMergeUnknownError"];
                            SetConvertStatus(errMsg, 0);
                            ShowError(errMsg);
                        } else {
                            ShowPublishedExportActions(publishResult, finalPath);
                            SetConvertStatus(
                                string.Format(S["StudioW12DoneFormat"], Path.GetFileName(finalPath), res.IntegratedLufs, res.MaxTruePeakDbtp),
                                1.0);
                        }
                        SetRunningUiState(false);
                    });
                }
            }
        });
    }

    void SetStudioExportControlsBusy(bool running) {
        if (studioBaseFormatSelector != null) {
            studioBaseFormatSelector.Enabled = !running;
            studioBaseFormatSelector.Alpha = running ? 0.55f : 1f;
        }
        for (int i = 0; i < studioTierPillBtns.Count; i++) {
            studioTierPillBtns[i].Enabled = !running;
            studioTierPillBtns[i].Alpha = running ? 0.55f : 1f;
        }
        if (studioSwitchEclipsaBtn != null) studioSwitchEclipsaBtn.Enabled = !running;
        if (studioOptAiUpmixSwitch != null) studioOptAiUpmixSwitch.Enabled = !running;
        if (studioBundleSelector != null) {
            studioBundleSelector.Enabled = !running;
            studioBundleSelector.Alpha = running ? 0.55f : 1f;
        }
        if (studioOptKaraokeSwitch != null) studioOptKaraokeSwitch.Enabled = !running;
        if (studioOptRemoveBackingSwitch != null) studioOptRemoveBackingSwitch.Enabled = !running;
        if (studioOptPerformanceSwitch != null) studioOptPerformanceSwitch.Enabled = !running;
        if (studioOptLosslessTwinSwitch != null) studioOptLosslessTwinSwitch.Enabled = !running;
        if (studioOptHeadphonePersonalSwitch != null) studioOptHeadphonePersonalSwitch.Enabled = !running;
        if (studioDynamicMotionSelector != null) {
            studioDynamicMotionSelector.Enabled = !running;
            studioDynamicMotionSelector.Alpha = running ? 0.55f : 1f;
        }
        if (studioOptEmbedCoverSwitch != null) studioOptEmbedCoverSwitch.Enabled = !running;
        if (studioOptEmbedLyricsSwitch != null) studioOptEmbedLyricsSwitch.Enabled = !running;
        if (studioExportFolderSelector != null) {
            studioExportFolderSelector.Enabled = !running;
            studioExportFolderSelector.Alpha = running ? 0.55f : 1f;
        }
        if (studioRecRecordBtn != null) studioRecRecordBtn.Enabled = !running;
        if (studioRecPreviewBtn != null) studioRecPreviewBtn.Enabled = !running;
        if (studioRecDiscardBtn != null) studioRecDiscardBtn.Enabled = !running;
    }

    static string FormatMmSs(double seconds) {
        int totalSec = Math.Max(0, (int)Math.Floor(seconds));
        return $"{totalSec / 60:00}:{totalSec % 60:00}";
    }
}
