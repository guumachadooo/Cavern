using Android.Animation;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;

using Cavern.Format.Common;

using Cavernize.Logic.Models;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    void ShowAudioSpecDialog() {
        CavernizeTrack active = GetActiveTrack();
        if (LoadedFile == null || active == null) {
            ShowToast(S["ActiveSourceEmpty"]);
            return;
        }

        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        bool dialogOpen = true;

        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(this);

        Cavernize.Logic.Spatial.LgsrDiagnostics lgsrDiag = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        bool twinSynced = lgsrDiag.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
        bool spatialActive = !twinSynced && (
            store.HeadphoneVirtualizer ||
            store.CavernizeUpmix ||
            store.MatrixUpmixing ||
            playerEngine?.IsLiveNeuralStemPlaybackActive == true);

        TextView abStateTv = null;
        TextView twinPill = null;

        // 0. Prominent Segmented A/B Comparison Header: [A: Original | B: ATMOS+FLAC / Spatial 3D]
        if (twinSynced || spatialActive) {
            LinearLayout abHeaderCard = new(this) { Orientation = Orientation.Vertical };
            abHeaderCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
            abHeaderCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

            LinearLayout abSegWrap = new(this) {
                Orientation = Orientation.Horizontal,
                BaselineAligned = false
            };
            abSegWrap.SetPadding(Dp(3), Dp(3), Dp(3), Dp(3));
            abSegWrap.Background = OneUiTheme.CreateRoundedDrawable(
                this, Color.Argb(34, 255, 255, 255), 999f, Color.Argb(44, 255, 255, 255), 0.9f);

            TextView segOriginal = new(this) {
                Text = S["AudioSpecAbOriginalSeg"],
                TextSize = 11.5f,
                Gravity = GravityFlags.Center
            };
            segOriginal.SetTypeface(sfBold, TypefaceStyle.Bold);
            segOriginal.SetIncludeFontPadding(false);
            segOriginal.SetSingleLine(true);
            segOriginal.Ellipsize = TextUtils.TruncateAt.End;
            segOriginal.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
            OneUiTheme.AttachRecoil(segOriginal, 0.95f);

            TextView segMaster = new(this) {
                Text = twinSynced ? S["AudioSpecAbTwinSeg"] : S["AudioSpecAbSpatialSeg"],
                TextSize = 11.5f,
                Gravity = GravityFlags.Center
            };
            segMaster.SetTypeface(sfBold, TypefaceStyle.Bold);
            segMaster.SetIncludeFontPadding(false);
            segMaster.SetSingleLine(true);
            segMaster.Ellipsize = TextUtils.TruncateAt.End;
            segMaster.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
            OneUiTheme.AttachRecoil(segMaster, 0.95f);

            abSegWrap.AddView(segOriginal, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            abSegWrap.AddView(segMaster, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            abHeaderCard.AddView(abSegWrap, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            abStateTv = Label("", 11.2f, OneUiTheme.TextSecondary);
            abStateTv.SetPadding(Dp(2), Dp(6), Dp(2), 0);
            abHeaderCard.AddView(abStateTv);

            void RefreshAbHeaderVisuals() {
                bool listeningOriginal = twinSynced
                    ? (playerEngine?.LgsrBypassed ?? false)
                    : !store.HeadphoneVirtualizer;

                segOriginal.Background = listeningOriginal
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.PeachText, 999f)
                    : null;
                segOriginal.SetTextColor(listeningOriginal ? Color.Rgb(16, 18, 26) : OneUiTheme.TextSecondary);

                Color masterBg = twinSynced ? OneUiTheme.IconSystemInfo : OneUiTheme.PrimaryHigh;
                segMaster.Background = !listeningOriginal
                    ? OneUiTheme.CreateRoundedDrawable(this, masterBg, 999f)
                    : null;
                segMaster.SetTextColor(!listeningOriginal ? Color.Rgb(16, 18, 26) : OneUiTheme.TextSecondary);

                if (twinSynced) {
                    abStateTv.Text = listeningOriginal ? S["AudioSpecLgsrAbOriginalHint"] : S["AudioSpecLgsrAbRestoredHint"];
                    abStateTv.SetTextColor(listeningOriginal ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo);
                    if (twinPill != null) {
                        twinPill.Text = listeningOriginal ? S["AudioSpecAtmosAbBadge"] : S["AudioSpecAtmosFlacBadge"];
                        twinPill.Background = OneUiTheme.CreateRoundedDrawable(
                            this, listeningOriginal ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo, 99f);
                    }
                } else {
                    abStateTv.Text = listeningOriginal ? S["AudioSpecAbSpatialOffHint"] : S["AudioSpecAbSpatialOnHint"];
                    abStateTv.SetTextColor(listeningOriginal ? OneUiTheme.PeachText : OneUiTheme.PrimaryHigh);
                }
            }

            void SelectAbMode(bool wantOriginal) {
                if (twinSynced) {
                    if (playerEngine == null) {
                        return;
                    }
                    playerEngine.LgsrBypassed = wantOriginal;
                    RefreshAbHeaderVisuals();
                    UpdateSpatialBadgePillVisuals();
                    RefreshLgsrUi();
                    RefreshSpatialXRayHud();
                } else {
                    SetHeadphoneVirtualizerState(!wantOriginal);
                    UpdateSpatialBadgePillVisuals();
                    RefreshAbHeaderVisuals();
                }
            }

            segOriginal.Click += (_, _) => SelectAbMode(wantOriginal: true);
            segMaster.Click += (_, _) => SelectAbMode(wantOriginal: false);
            RefreshAbHeaderVisuals();

            box.AddView(abHeaderCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });
        }

        // 1. Primary Format & Spatial Engine Summary Card (read-only binaural engine line, no redundant profile button)
        LinearLayout specCard = new(this) { Orientation = Orientation.Vertical };
        specCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
        specCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        TextView fmtHdr = Label(active.FormatHeader, 14f, ColText);
        fmtHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
        specCard.AddView(fmtHdr);

        int beds = trackSourceItems.Count(x => x.IsBed);
        int objs = trackSourceItems.Count(x => x.IsDynamicObject);
        int srHz = playerEngine?.OutputSampleRate ?? 48000;
        string spatialLine = objs > 0
            ? string.Format(S["AudioSpecSpatialChObjsFmt"], beds, objs, srHz)
            : string.Format(S["AudioSpecChannelsFmt"], beds, srHz);
        TextView spatialTv = Label(spatialLine, 12f, OneUiTheme.PrimaryHigh);
        spatialTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
        spatialTv.SetPadding(0, Dp(2), 0, Dp(4));
        specCard.AddView(spatialTv);

        TextView binLineTv = Label($"• {S["BinauralProfileActive"]}: {BinauralProfileService.GetActiveDisplayTitle(store, S)}", 11.5f, OneUiTheme.TextSecondary);
        specCard.AddView(binLineTv);

        foreach (var d in active.Details.Take(6)) {
            TextView dTv = Label($"• {d.property}: {d.value}", 11.5f, OneUiTheme.TextSecondary);
            specCard.AddView(dTv);
        }
        box.AddView(specCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 2. Scientific Audio Quality Analysis Card (True/Fake 24-bit, FFT cutoff, Dynamic Range, Clipping)
        string activePath = currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "";
        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        LibraryTrackItem libItem = snap?.Tracks?.FirstOrDefault(t => string.Equals(t.FilePath, activePath, StringComparison.OrdinalIgnoreCase))
            ?? new LibraryTrackItem {
                FilePath = activePath,
                FormatBadge = Path.GetExtension(activePath).TrimStart('.').ToUpperInvariant(),
                AudioBadge = active.FormatHeader,
                ChannelCount = beds + objs,
                IsSpatial = objs > 0,
                FileSizeBytes = File.Exists(activePath) ? new FileInfo(activePath).Length : 0,
                LastModifiedTicks = File.Exists(activePath) ? File.GetLastWriteTimeUtc(activePath).Ticks : 0
            };

        LinearLayout qualCard = new(this) { Orientation = Orientation.Vertical };
        qualCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
        qualCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        void PopulateQualCard(bool triggerBackground) {
            qualCard.RemoveAllViews();
            AudioQualityReport qReport = AudioQualityAnalyzer.EvaluateFast(libItem, this);
            string tierName = qReport.Category switch {
                AudioTierCategory.Atmos => S["TierAtmos"],
                AudioTierCategory.Surround => S["TierSurround"],
                _ => S["TierStereo"]
            };
            TextView qualHdr = Label($"{tierName} • {string.Format(S["QualityScoreLabel"], (int)Math.Round(qReport.Score))}", 12.5f, OneUiTheme.PeachText);
            qualHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            qualHdr.SetPadding(0, 0, 0, Dp(4));
            qualCard.AddView(qualHdr);

            if (qReport.DeepAnalyzed) {
                if (qReport.EffectiveBitDepth > 0) {
                    string bitStr = qReport.IsFake24Bit
                        ? S["QualityFake24BitWarn"]
                        : (qReport.EffectiveBitDepth >= 20
                            ? string.Format(S["QualityTrue24BitOk"], qReport.EffectiveBitDepth)
                            : string.Format(S["QualityEffectiveBits"], qReport.EffectiveBitDepth));
                    Color bitCol = qReport.IsFake24Bit ? OneUiTheme.FunctionalRedFill : OneUiTheme.TextSecondary;
                    qualCard.AddView(Label($"• {bitStr}", 11.5f, bitCol));
                }
                if (qReport.SpectralCutoffHz > 0) {
                    string cutStr = string.Format(S["QualitySpectralCutoff"], (qReport.SpectralCutoffHz / 1000.0).ToString("0.0"));
                    qualCard.AddView(Label($"• {cutStr}", 11.5f, OneUiTheme.TextSecondary));
                }
                if (qReport.IsFakeLossless) {
                    qualCard.AddView(Label($"• {S["QualityFakeLosslessWarn"]}", 11.5f, OneUiTheme.FunctionalRedFill));
                } else if (qReport.IsUpsampledHiRes) {
                    qualCard.AddView(Label($"• {S["QualityUpsampledHiResWarn"]}", 11.5f, OneUiTheme.PeachText));
                }
                if (qReport.DynamicRangeDb > 0.5) {
                    qualCard.AddView(Label($"• {string.Format(S["QualityDynamicRange"], qReport.DynamicRangeDb.ToString("0.0"))}", 11.5f, OneUiTheme.TextSecondary));
                }
                if (qReport.ClippedPercent >= 0.05) {
                    qualCard.AddView(Label($"• {string.Format(S["QualityClippingWarn"], qReport.ClippedPercent.ToString("0.00"))}", 11.5f, OneUiTheme.FunctionalRedFill));
                }
            } else {
                TextView pendingTv = Label(triggerBackground ? S["QualityAnalyzing"] : S["QualityFastOnlyNote"], 11.5f, OneUiTheme.TextMuted);
                qualCard.AddView(pendingTv);
                if (triggerBackground) {
                    string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
                    AudioQualityAnalyzer.AnalyzeVariantsInBackground(this, [libItem], ffLoc, () => {
                        RunOnUiThread(() => {
                            if (uiDestroyed || !dialogOpen || dlg == null || !dlg.IsShowing) {
                                return;
                            }
                            PopulateQualCard(triggerBackground: false);
                        });
                    });
                }
            }
        }
        PopulateQualCard(triggerBackground: true);

        box.AddView(qualCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 2B. W09/W14 Universal Lossless Twin & LGSR Status Card + Collapsible Pro Signal Telemetry
        if (lgsrDiag.State != Cavernize.Logic.Spatial.LosslessTwinVerificationState.None) {
            LinearLayout lgsrCard = new(this) { Orientation = Orientation.Vertical };
            lgsrCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
            lgsrCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

            LinearLayout lgsrHdrRow = HRow();
            lgsrHdrRow.SetPadding(0, 0, 0, Dp(4));
            TextView lgsrHdr = Label(S["AudioSpecLgsrHeader"], 12.5f, OneUiTheme.IconSystemInfo);
            lgsrHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            lgsrHdr.SetSingleLine(true);
            lgsrHdr.Ellipsize = TextUtils.TruncateAt.End;
            lgsrHdrRow.AddView(lgsrHdr, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            if (twinSynced) {
                twinPill = new TextView(this) {
                    Text = playerEngine.LgsrBypassed ? S["AudioSpecAtmosAbBadge"] : S["AudioSpecAtmosFlacBadge"],
                    TextSize = 10f,
                    Gravity = GravityFlags.Center
                };
                twinPill.SetTypeface(sfBold, TypefaceStyle.Bold);
                twinPill.SetSingleLine(true);
                twinPill.SetPadding(Dp(8), Dp(3), Dp(8), Dp(3));
                twinPill.SetTextColor(Color.Rgb(14, 18, 26));
                twinPill.Background = OneUiTheme.CreateRoundedDrawable(
                    this,
                    playerEngine.LgsrBypassed ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo,
                    99f);
                lgsrHdrRow.AddView(twinPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                    LeftMargin = Dp(6)
                });
                lgsrCard.AddView(lgsrHdrRow);

                string twinLabel = string.IsNullOrEmpty(lgsrDiag.TwinFormatSummary)
                    ? lgsrDiag.TwinFileName
                    : $"{lgsrDiag.TwinFormatSummary} • {lgsrDiag.TwinFileName}";
                lgsrCard.AddView(Label($"• {string.Format(S["AudioSpecLgsrActive"], twinLabel)}", 11.5f, ColText));

                // Collapsible Pro Signal Telemetry disclosure (DriftPpm, Coherence, EnvelopeLock)
                bool telemetryExpanded = false;
                LinearLayout telemetryToggleRow = HRow();
                telemetryToggleRow.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
                telemetryToggleRow.Background = OneUiTheme.CreateRoundedDrawable(
                    this, Color.Argb(24, 255, 255, 255), 12f, Color.Argb(36, 255, 255, 255), 0.8f);
                OneUiTheme.AttachRecoil(telemetryToggleRow, 0.97f);

                TextView telemetryTitle = Label(S["AudioSpecProTelemetryToggle"], 11.2f, OneUiTheme.TextSecondary);
                telemetryTitle.SetTypeface(sfSemi, TypefaceStyle.Bold);
                telemetryTitle.SetSingleLine(true);
                telemetryTitle.Ellipsize = TextUtils.TruncateAt.End;
                OneUiIconView telemetryChevron = new(this, OneUiIconType.ChevronDown, OneUiTheme.TextMuted);
                telemetryToggleRow.AddView(telemetryTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
                telemetryToggleRow.AddView(telemetryChevron, new LinearLayout.LayoutParams(Dp(14), Dp(14)));

                LinearLayout telemetryBody = new(this) {
                    Orientation = Orientation.Vertical,
                    Visibility = ViewStates.Gone
                };
                telemetryBody.SetPadding(Dp(2), Dp(6), Dp(2), 0);
                telemetryBody.AddView(Label($"• {string.Format(S["AudioSpecLgsrSync"], lgsrDiag.SyncOffsetMs, lgsrDiag.DriftPpm, lgsrDiag.SyncConfidence * 100f)}", 11.2f, OneUiTheme.TextSecondary));
                telemetryBody.AddView(Label($"• {string.Format(S["AudioSpecLgsrBands"], lgsrDiag.CouplingStartHz / 1000f, lgsrDiag.SpxStartHz / 1000f, lgsrDiag.AverageCoherence * 100f)}", 11.2f, OneUiTheme.TextSecondary));
                telemetryBody.AddView(Label($"• {string.Format(S["AudioSpecLgsrGuards"], lgsrDiag.DynamicEnvelopeLockDb, lgsrDiag.ClippingGuardActive ? S["StatusOnTag"] : "OK")}", 11.2f, OneUiTheme.TextSecondary));

                telemetryToggleRow.Click += (_, _) => {
                    telemetryExpanded = !telemetryExpanded;
                    telemetryBody.Visibility = telemetryExpanded ? ViewStates.Visible : ViewStates.Gone;
                    telemetryChevron.IconType = telemetryExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
                };

                lgsrCard.AddView(telemetryToggleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(6)
                });
                lgsrCard.AddView(telemetryBody);
            } else {
                lgsrCard.AddView(lgsrHdrRow);
                string stateMsg = lgsrDiag.State switch {
                    Cavernize.Logic.Spatial.LosslessTwinVerificationState.Searching => S["LgsrStatusSearching"],
                    Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless => S["LgsrStatusBypassedLossless"],
                    Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedFakeLossless => S["LgsrStatusBypassedFake"],
                    _ => S["LgsrStatusBypassedLowConf"]
                };
                lgsrCard.AddView(Label($"• {stateMsg}", 11.5f, OneUiTheme.TextSecondary));

                if (lgsrDiag.State != Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless) {
                    LinearLayout rescanRow = HRow();
                    rescanRow.SetPadding(0, Dp(6), 0, 0);
                    Button rescanBtn = PillButton(S["LgsrRescanTwinBtn"], () => {
                        StartLosslessTwinDiscoveryAsync(active);
                        dlg?.Dismiss();
                    });
                    rescanRow.AddView(rescanBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                    lgsrCard.AddView(rescanRow);
                }
            }

            box.AddView(lgsrCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = LoadedFile.Tracks.Count > 1 ? Dp(10) : 0
            });
        }

        // 3. Audio Streams Selector (only if container holds multiple audio tracks)
        if (LoadedFile.Tracks.Count > 1) {
            TextView stHdr = Label(S["AudioStreamsSection"], 12.5f, ColText);
            stHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            stHdr.SetPadding(Dp(2), 0, Dp(2), Dp(6));
            box.AddView(stHdr);

            for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
                CavernizeTrack tr = LoadedFile.Tracks[i];
                bool isSel = tr == selectedTrack;
                LinearLayout row = HRow();
                row.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 14f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                OneUiTheme.AttachRecoil(row, 0.98f);
                TextView nameTv = Label($"#{i + 1}: {tr}", 12f, isSel ? ColText : OneUiTheme.TextSecondary);
                nameTv.SetTypeface(isSel ? sfBold : sfSemi, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                row.Click += (_, _) => {
                    dlg?.Dismiss();
                    if (SelectedTrack != tr) {
                        SelectedTrack = tr;
                    }
                };
                box.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(5)
                });
            }
        }

        dlg = OneUiModalDialog.Show(
            this,
            S["AudioSpecTitle"],
            null,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["CloseBtn"],
            customContent: box);
        dlg.DismissEvent += (_, _) => dialogOpen = false;
    }

    void RefreshMusicPlayerControlsUi() {
        if (musicWaveSeekBar == null) {
            return;
        }
        bool isPlaying = playerEngine?.IsPlaying ?? false;
        double curSec = playerEngine?.CurrentSeconds ?? 0;
        double lyricsSec = playerEngine?.HeardSeconds ?? curSec;
        double totSec = playerEngine?.TotalSeconds ?? 0;

        musicArtworkView?.SetPlaying(isPlaying);
        if (musicStageMode == PlayerStageMode.Video && ((activeTab == 0 && !isLibrarySectionOpen) || fullscreenVideoDlg != null)) {
            string trkStr = LoadedFile != null && LoadedFile.Tracks.Count > 1
                ? $"{Math.Max(1, LoadedFile.Tracks.ToList().IndexOf(selectedTrack) + 1)}/{LoadedFile.Tracks.Count}"
                : "";
            musicVideoStage?.SyncPlaybackState(isPlaying, curSec, totSec);
            musicVideoStage?.ConfigureMetadata(
                musicTitleText.Text,
                musicSpatialBadgeText.Text,
                trkStr,
                LoadedFile?.Tracks.Count > 1,
                activeSubtitleCues.Count > 0,
                playerEngine?.Loop ?? false,
                S["VideoUnlockHint"]);
        } else if (musicStageMode == PlayerStageMode.Lyrics && activeTab == 0 && !isLibrarySectionOpen) {
            musicLyricsView?.UpdatePlayback(lyricsSec, isPlaying);
        }
        if (musicPlayIcon != null) {
            musicPlayIcon.IconType = isPlaying ? OneUiIconType.Pause : OneUiIconType.Play;
        }

        RefreshBottomActionBarVisuals();
    }

    void StartBackgroundOnlineEnrichment(MediaArtworkMetadata meta) {
        if (meta == null) return;
        int myGen = ++onlineEnrichGeneration;
        Task.Run(async () => {
            try {
                OnlineEnrichResult res = await OnlineMetadataService.AutoEnrichIfMissingAsync(this, meta).ConfigureAwait(false);
                if (res == null) return;

                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != onlineEnrichGeneration || currentMediaMeta != meta) {
                        res.ArtworkBitmap?.Dispose();
                        return;
                    }
                    bool changed = false;
                    if (res.ArtworkBitmap != null) {
                        if (meta.ArtworkBitmap != null && meta.ArtworkBitmap != res.ArtworkBitmap) {
                            meta.ArtworkBitmap.Dispose();
                        }
                        meta.ArtworkBitmap = res.ArtworkBitmap;
                        meta.HasEmbeddedArtwork = true;
                        MediaMetadataHelper.ExtractPalette(res.ArtworkBitmap, out Color p, out Color s);
                        meta.PrimaryAccent = p;
                        meta.SecondaryAccent = s;
                        changed = true;
                    }
                    if (res.Lyrics != null && res.Lyrics.HasLyrics) {
                        meta.Lyrics = res.Lyrics;
                        changed = true;
                        NotifyLyricsMetadataUpdated();
                    }
                    if (changed) {
                        RefreshMusicPlayerUi();
                        mediaNotification?.UpdateMetadata(
                            meta.Title,
                            string.IsNullOrWhiteSpace(meta.Artist) ? "Cavernize 3D" : meta.Artist,
                            meta.Album ?? "",
                            playerEngine?.TotalSeconds ?? 0,
                            meta.ArtworkBitmap);
                    }
                });
            } catch (Exception ex) {
                AppLogger.Warn($"Online auto-enrich: {ex.Message}");
            }
        });
    }


    // ============================================================
    // ONLINE METADATA, COVER ART, SYNCED LYRICS & SUBTITLE SEARCH
    // ============================================================
    void ShowOnlineMetadataDialog() {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };

        EditText MakeModalInput(string initialText, string hint) {
            EditText et = new(this) {
                Text = initialText ?? "",
                Hint = hint ?? "",
                TextSize = 13f
            };
            et.SetTextColor(ColText);
            et.SetHintTextColor(ColMuted);
            et.SetSingleLine(true);
            et.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            et.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
            return et;
        }

        string defTrack = OnlineMetadataService.CleanSearchTerm(
            !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                ? currentMediaMeta.Title
                : (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : ""));
        string defArtist = currentMediaMeta?.Artist ?? "";
        string defAlbum = currentMediaMeta?.Album ?? "";

        TextView trackLbl = Label(S["OnlineTrackLabel"], 11f, OneUiTheme.TextMuted);
        trackLbl.SetPadding(Dp(2), 0, Dp(2), Dp(3));
        box.AddView(trackLbl);
        EditText queryInput = MakeModalInput(defTrack, S["OnlineSearchQueryHint"]);
        box.AddView(queryInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView artistLbl = Label(S["OnlineArtistLabel"], 11f, OneUiTheme.TextMuted);
        artistLbl.SetPadding(Dp(2), 0, Dp(2), Dp(3));
        box.AddView(artistLbl);
        EditText artistInput = MakeModalInput(defArtist, "");
        box.AddView(artistInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView albumLbl = Label(S["OnlineAlbumLabel"], 11f, OneUiTheme.TextMuted);
        albumLbl.SetPadding(Dp(2), 0, Dp(2), Dp(3));
        box.AddView(albumLbl);
        EditText albumInput = MakeModalInput(defAlbum, "");
        box.AddView(albumInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(box, S["OnlineAutoBestSwitch"], store.OnlineAutoSelectBestMatch, v => store.OnlineAutoSelectBestMatch = v);

        LinearLayout resultsHost = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void ApplyCoverCandidate(OnlineCoverCandidate c, string fallbackTitle) {
            dlg?.Dismiss();
            string tName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : fallbackTitle;
            string arName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist) ? currentMediaMeta.Artist : c.Artist;
            string alName = !string.IsNullOrWhiteSpace(albumInput.Text) ? albumInput.Text.Trim()
                : (!string.IsNullOrWhiteSpace(currentMediaMeta?.Album) ? currentMediaMeta.Album : c.Album);
            string pathOvr = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path;
            bool autoPicked = store.OnlineAutoSelectBestMatch && c.IsExactMatch;
            Task.Run(async () => {
                var (bmp, _) = await OnlineMetadataService.DownloadAndCacheCoverAsync(
                    this, c.ImageUrl, tName, arName, alName, filePathOverride: pathOvr).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed || !IsBitmapUsable(bmp)) return;
                    if (!string.IsNullOrEmpty(pathOvr)) {
                        MediaLibraryService.InvalidateThumbCache(pathOvr);
                    }
                    if (currentMediaMeta != null) {
                        currentMediaMeta.ArtworkBitmap = bmp;
                        currentMediaMeta.HasEmbeddedArtwork = true;
                        MediaMetadataHelper.ExtractPalette(bmp, out Color p, out Color s);
                        currentMediaMeta.PrimaryAccent = p;
                        currentMediaMeta.SecondaryAccent = s;
                    }
                    RefreshMusicPlayerUi();
                    if (isLibrarySectionOpen) {
                        RenderLibraryView();
                    }
                    mediaNotification?.UpdateMetadata(
                        currentMediaMeta?.Title ?? tName,
                        string.IsNullOrWhiteSpace(arName) ? "Cavernize 3D" : arName,
                        alName ?? "",
                        playerEngine?.TotalSeconds ?? 0,
                        bmp);
                    string msg = autoPicked
                        ? string.Format(S["OnlineAutoBestAppliedCover"], c.Source, c.Artist, c.Title)
                        : S["OnlineAppliedCover"];
                    Toast.MakeText(this, msg, ToastLength.Short).Show();
                });
            });
        }

        void ApplyLyricsCandidate(OnlineLyricsCandidate c, bool autoPicked) {
            dlg?.Dismiss();
            string tName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : c.TrackName;
            string arName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist) ? currentMediaMeta.Artist : c.ArtistName;
            OnlineMetadataService.SaveLyricsToCache(this, tName, arName, c.BestContent);
            LyricsDocument doc = LyricsService.ParseLyricsText(c.BestContent, c.Source);
            if (currentMediaMeta != null) {
                currentMediaMeta.Lyrics = doc;
            }
            NotifyLyricsMetadataUpdated();
            RefreshMusicPlayerUi();
            SetPlayerStageMode(PlayerStageMode.Lyrics);
            string msg = autoPicked
                ? string.Format(S["OnlineAutoBestAppliedLyrics"], c.Source, c.ArtistName, c.TrackName)
                : S["OnlineAppliedLyrics"];
            Toast.MakeText(this, msg, ToastLength.Short).Show();
        }

        static int GetCandidateRiskPct(OnlineLyricsCandidate c) {
            double raw = c.MatchRiskScore;
            if ((raw > 0.0 && raw < 1.0) || (raw == 1.0 && !c.IsExactMatch && c.MatchScore < 200)) {
                return Math.Clamp((int)Math.Round(raw * 100.0), 0, 100);
            }
            return Math.Clamp((int)Math.Round(raw), 0, 100);
        }

        TextView MakeCandidateBadgePill(string text, Color accent) {
            TextView pill = Label(text, 10f, accent);
            pill.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
            pill.SetIncludeFontPadding(false);
            pill.SetSingleLine(true);
            pill.Ellipsize = TextUtils.TruncateAt.End;
            pill.SetPadding(Dp(7), Dp(3), Dp(7), Dp(3));
            pill.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(accent, 34), 999f, OneUiTheme.WithAlpha(accent, 118), 0.8f);
            return pill;
        }

        void RunCoverSearch() {
            string searchTitle = queryInput.Text?.Trim() ?? "";
            string searchArtist = artistInput.Text?.Trim() ?? "";
            string searchAlbum = albumInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(searchTitle) && string.IsNullOrEmpty(searchAlbum)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            bool isVid = currentMediaMeta?.HasVideo == true;
            string q = string.IsNullOrWhiteSpace(searchTitle) ? searchAlbum : searchTitle;

            Task.Run(async () => {
                List<OnlineCoverCandidate> covers = await OnlineMetadataService.SearchCoverCandidatesAsync(
                    q, searchArtist, searchAlbum, isVid).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    resultsHost.RemoveAllViews();
                    if (covers.Count == 0) {
                        resultsHost.AddView(Label(S["OnlineNoResults"], 12f, ColMuted));
                        return;
                    }

                    OnlineCoverCandidate exactBest = store.OnlineAutoSelectBestMatch
                        ? covers.FirstOrDefault(c => c.IsExactMatch)
                        : null;
                    if (exactBest != null) {
                        ApplyCoverCandidate(exactBest, q);
                        return;
                    }

                    if (store.OnlineAutoSelectBestMatch) {
                        TextView hint = Label(S["OnlineNoExactFallback"], 11.5f, OneUiTheme.PeachText);
                        hint.SetPadding(Dp(2), 0, Dp(2), Dp(6));
                        resultsHost.AddView(hint);
                    }

                    foreach (OnlineCoverCandidate c in covers) {
                        LinearLayout row = HRow();
                        row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = c.IsExactMatch ? S["OnlineExactMatchBadge"] : "";
                        TextView t1 = Label($"{c.Title}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        TextView t2 = Label($"{c.Artist} • {c.Album} ({c.Source})", 11f, c.IsExactMatch ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
                        col.AddView(t1);
                        col.AddView(t2);
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplyCoverCandidate(c, q);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        void RunLyricsSearch() {
            string titleArg = queryInput.Text?.Trim() ?? "";
            string artistArg = artistInput.Text?.Trim() ?? "";
            string albumArg = albumInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(titleArg)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            double durArg = playerEngine?.TotalSeconds ?? 0;

            Task.Run(async () => {
                List<OnlineLyricsCandidate> cands = await OnlineMetadataService.SearchLyricsAsync(
                    titleArg, artistArg, albumArg, expectedDurationSec: durArg).ConfigureAwait(false);
                if (cands.Count == 0 && (!string.IsNullOrEmpty(artistArg) || !string.IsNullOrEmpty(albumArg))) {
                    cands = await OnlineMetadataService.SearchLyricsAsync(titleArg, "", "", expectedDurationSec: durArg).ConfigureAwait(false);
                }
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    resultsHost.RemoveAllViews();
                    if (cands.Count == 0) {
                        resultsHost.AddView(Label(S["OnlineNoResults"], 12f, ColMuted));
                        return;
                    }

                    OnlineLyricsCandidate exactBest = store.OnlineAutoSelectBestMatch
                        ? (cands.FirstOrDefault(c => (c.IsSyllableSynced || c.IsSynced) && (c.IsExactMatch || GetCandidateRiskPct(c) <= 25))
                           ?? cands.FirstOrDefault(c => c.IsExactMatch))
                        : null;
                    if (exactBest != null) {
                        ApplyLyricsCandidate(exactBest, autoPicked: true);
                        return;
                    }

                    if (store.OnlineAutoSelectBestMatch) {
                        TextView hint = Label(S["OnlineNoExactFallback"], 11.5f, OneUiTheme.PeachText);
                        hint.SetPadding(Dp(2), 0, Dp(2), Dp(6));
                        resultsHost.AddView(hint);
                    }

                    foreach (OnlineLyricsCandidate c in cands) {
                        LinearLayout row = HRow();
                        row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = c.IsExactMatch ? S["OnlineExactMatchBadge"] : "";
                        TextView t1 = Label($"{c.TrackName} — {c.ArtistName}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        col.AddView(t1);

                        LinearLayout badgeRow = HRow();
                        badgeRow.SetPadding(0, Dp(4), 0, Dp(3));
                        string tierText = c.IsSyllableSynced
                            ? string.Format(S["OnlineSyllableLyricsBadge"], c.Source)
                            : (c.IsSynced
                                ? string.Format(S["OnlineSyncedLyricsBadge"], c.Source)
                                : string.Format(S["OnlinePlainLyricsBadge"], c.Source));
                        Color tierColor = c.IsSyllableSynced
                            ? OneUiTheme.IconSystemInfo
                            : (c.IsSynced ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
                        badgeRow.AddView(MakeCandidateBadgePill(tierText, tierColor),
                            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

                        int riskPct = GetCandidateRiskPct(c);
                        int confPct = Math.Clamp(100 - riskPct, 0, 100);
                        string riskText = riskPct <= 20
                            ? string.Format(S["OnlineRiskLowBadge"], confPct)
                            : (riskPct <= 50
                                ? string.Format(S["OnlineRiskMedBadge"], confPct)
                                : string.Format(S["OnlineRiskHighBadge"], confPct));
                        Color riskColor = riskPct <= 20
                            ? OneUiTheme.FunctionalGreen
                            : (riskPct <= 50 ? OneUiTheme.PeachText : OneUiTheme.FunctionalOrange);
                        badgeRow.AddView(MakeCandidateBadgePill(riskText, riskColor),
                            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                        col.AddView(badgeRow);

                        string durTag = c.DurationSec > 1 ? $" • {TimeSpan.FromSeconds(c.DurationSec):mm\\:ss}" : "";
                        string subLine = !string.IsNullOrWhiteSpace(c.AlbumName) ? $"{c.AlbumName}{durTag}" : durTag.TrimStart(' ', '•');
                        if (!string.IsNullOrWhiteSpace(subLine)) {
                            TextView t2 = Label(subLine, 11f, OneUiTheme.TextMuted);
                            col.AddView(t2);
                        }
                        if (!string.IsNullOrWhiteSpace(c.MatchRiskNotes)) {
                            TextView riskNoteTv = Label(c.MatchRiskNotes, 10.5f, riskPct > 50 ? OneUiTheme.FunctionalOrange : OneUiTheme.TextMuted);
                            riskNoteTv.SetPadding(0, Dp(2), 0, 0);
                            col.AddView(riskNoteTv);
                        }
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplyLyricsCandidate(c, autoPicked: false);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        if (currentMediaMeta?.Lyrics is { HasLyrics: true } curLyr) {
            string curSrc = string.IsNullOrWhiteSpace(curLyr.SourceLabel) ? S["OnlineLyricsSourceLocal"] : curLyr.SourceLabel;
            Color bannerAccent = curLyr.IsSyllableSynced
                ? OneUiTheme.IconSystemInfo
                : (curLyr.IsSynced ? OneUiTheme.PrimaryHigh : OneUiTheme.PeachText);
            string bannerText = curLyr.IsSyllableSynced
                ? string.Format(S["OnlineCurrentLyricsSyllableBanner"], curSrc)
                : (curLyr.IsSynced
                    ? string.Format(S["OnlineCurrentLyricsLineBanner"], curSrc)
                    : string.Format(S["OnlineCurrentLyricsPlainBanner"], curSrc));

            LinearLayout curLyrBanner = HRow();
            curLyrBanner.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
            curLyrBanner.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(bannerAccent, 24), 12f, OneUiTheme.WithAlpha(bannerAccent, 110), 0.9f);
            TextView curLyrTv = Label(bannerText, 11.2f, bannerAccent);
            curLyrBanner.AddView(curLyrTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (!curLyr.IsSyllableSynced) {
                TextView upgPill = MakeCandidateBadgePill(S["OnlineUpgradeLyricsBtn"], bannerAccent);
                curLyrBanner.AddView(upgPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });
                OneUiTheme.AttachRecoil(curLyrBanner, 0.97f);
                curLyrBanner.Click += (_, _) => RunLyricsSearch();
            }
            box.AddView(curLyrBanner, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });
        }

        LinearLayout actionBtns = HRow();
        actionBtns.SetPadding(0, 0, 0, Dp(10));
        Button findCoverBtn = PrimaryButton(S["OnlineCoverBtn"], RunCoverSearch);
        Button findLyricsBtn = PillButton(S["OnlineLyricsBtn"], RunLyricsSearch);
        actionBtns.AddView(findCoverBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        actionBtns.AddView(findLyricsBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        box.AddView(actionBtns);
        box.AddView(resultsHost);

        dlg = OneUiModalDialog.Show(
            this,
            S["OnlineSearchTitle"],
            null,
            icon: OneUiIconType.Globe,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: box);
    }

    void ShowSubtitlesDialog() {
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void AddOptionRow(string labelText, bool selected, Action onClick) {
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = selected
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 85), 16f, OneUiTheme.PrimaryHigh, 1f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
            OneUiTheme.AttachRecoil(row, 0.97f);
            TextView tv = Label(labelText, 13f, selected ? ColText : OneUiTheme.TextSecondary);
            tv.SetTypeface(null, selected ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                dlg?.Dismiss();
                onClick?.Invoke();
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        AddOptionRow(S["SubtitlesOff"], string.IsNullOrEmpty(activeSubtitleTrackId), () => {
            activeSubtitleTrackId = null;
            activeSubtitleCues = [];
            musicVideoStage?.SetSubtitles(activeSubtitleCues);
            RefreshBottomActionBarVisuals();
        });

        for (int i = 0; i < discoveredSubtitles.Count; i++) {
            SubtitleTrackInfo tr = discoveredSubtitles[i];
            bool isSel = string.Equals(activeSubtitleTrackId, tr.Id, StringComparison.Ordinal);
            AddOptionRow(tr.Title, isSel, () => SelectSubtitleTrack(tr));
        }

        if (discoveredSubtitles.Count == 0) {
            TextView emptyTv = Label(S["SubtitlesNoneFound"], 12f, OneUiTheme.TextMuted);
            emptyTv.SetPadding(Dp(4), Dp(2), Dp(4), Dp(8));
            list.AddView(emptyTv);
        }

        Button onlineSubBtn = PrimaryButton(S["OnlineSubsBtn"], () => {
            dlg?.Dismiss();
            ShowOnlineSubtitlesSearchDialog();
        });
        list.AddView(onlineSubBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        Button importBtn = PillButton(S["SubtitlesImportBtn"], () => {
            dlg?.Dismiss();
            OpenFileWithExplorerForExtensions(
                S["VideoSubtitlesTitle"],
                [".srt", ".vtt", ".ass", ".ssa", ".sub", ".txt"],
                LoadExternalSubtitleFromPath,
                ReqLoadSubtitles);
        });
        list.AddView(importBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        dlg = OneUiModalDialog.Show(
            this,
            S["VideoSubtitlesTitle"],
            null,
            icon: OneUiIconType.SubtitlesCc,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: list);
    }

    void ShowOnlineSubtitlesSearchDialog() {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        string defaultQ = OnlineMetadataService.CleanSearchTerm(
            currentMediaMeta?.Title ?? (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : ""));

        EditText qInput = new(this) {
            Text = defaultQ,
            Hint = S["OnlineSearchQueryHint"],
            TextSize = 13.5f
        };
        qInput.SetTextColor(ColText);
        qInput.SetHintTextColor(ColMuted);
        qInput.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        qInput.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
        box.AddView(qInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(box, S["OnlineAutoBestSwitch"], store.OnlineAutoSelectBestMatch, v => store.OnlineAutoSelectBestMatch = v);

        LinearLayout resultsHost = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void ApplySubtitleCandidate(OnlineSubtitleCandidate cand, bool autoPicked) {
            dlg?.Dismiss();
            Task.Run(async () => {
                string path = await OnlineMetadataService.DownloadAndExtractSubtitleAsync(this, cand).ConfigureAwait(false);
                if (string.IsNullOrEmpty(path)) return;
                List<SubtitleCue> cues = SubtitleService.LoadFromFilePath(path);
                RunOnUiThread(() => {
                    if (uiDestroyed || cues.Count == 0) return;
                    SubtitleTrackInfo tr = new() {
                        Id = "ext:" + path,
                        Title = $"{cand.Title} ({cand.Language})",
                        Language = cand.Language,
                        ExternalFilePath = path
                    };
                    discoveredSubtitles.RemoveAll(s => string.Equals(s.Id, tr.Id, StringComparison.OrdinalIgnoreCase));
                    discoveredSubtitles.Insert(0, tr);
                    activeSubtitleTrackId = tr.Id;
                    activeSubtitleCues = cues;
                    musicVideoStage?.SetSubtitles(activeSubtitleCues);
                    RefreshBottomActionBarVisuals();
                    string msg = autoPicked
                        ? string.Format(S["OnlineAutoBestAppliedSub"], cand.Source, cand.Title)
                        : S["OnlineAppliedSub"];
                    Toast.MakeText(this, msg, ToastLength.Short).Show();
                });
            });
        }

        void ExecuteSubSearch() {
            string q = qInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            Task.Run(async () => {
                List<OnlineSubtitleCandidate> list = await OnlineMetadataService.SearchSubtitlesAsync(q, S["SubtitlesSearchLangCode"]).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    resultsHost.RemoveAllViews();
                    if (list.Count == 0) {
                        resultsHost.AddView(Label(S["OnlineNoResults"], 12f, ColMuted));
                        return;
                    }

                    OnlineSubtitleCandidate exactBest = store.OnlineAutoSelectBestMatch
                        ? list.FirstOrDefault(s => s.IsExactMatch)
                        : null;
                    if (exactBest != null) {
                        ApplySubtitleCandidate(exactBest, autoPicked: true);
                        return;
                    }

                    if (store.OnlineAutoSelectBestMatch) {
                        TextView hint = Label(S["OnlineNoExactFallback"], 11.5f, OneUiTheme.PeachText);
                        hint.SetPadding(Dp(2), 0, Dp(2), Dp(6));
                        resultsHost.AddView(hint);
                    }

                    foreach (OnlineSubtitleCandidate cand in list) {
                        LinearLayout row = HRow();
                        row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = cand.IsExactMatch ? S["OnlineExactMatchBadge"] : "";
                        TextView t1 = Label($"{cand.Title}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        TextView t2 = Label($"{cand.Language} • {cand.Source}", 11f, OneUiTheme.PrimaryHigh);
                        col.AddView(t1);
                        col.AddView(t2);
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplySubtitleCandidate(cand, autoPicked: false);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        Button searchBtn = PrimaryButton(S["OnlineSearchAction"], ExecuteSubSearch);
        box.AddView(searchBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });
        box.AddView(resultsHost);

        dlg = OneUiModalDialog.Show(
            this,
            S["OnlineSubsBtn"],
            null,
            icon: OneUiIconType.SubtitlesCc,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: box);

        if (!string.IsNullOrWhiteSpace(defaultQ)) {
            ExecuteSubSearch();
        }
    }

    void SelectSubtitleTrack(SubtitleTrackInfo track) {
        if (track == null) return;
        string mediaPath = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path;
        Task.Run(() => {
            List<SubtitleCue> cues = SubtitleService.LoadCues(this, track, mediaPath, ffmpeg);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                activeSubtitleTrackId = track.Id;
                activeSubtitleCues = cues;
                musicVideoStage?.SetSubtitles(activeSubtitleCues);
                RefreshBottomActionBarVisuals();
                if (cues.Count == 0) {
                    ShowError(S["SubtitlesNoneFound"]);
                }
            });
        });
    }

    static View FindFullscreenVideoHudOverlay(SyncedVideoStageView stage) {
        if (stage == null) {
            return null;
        }
        for (int i = 0; i < stage.ChildCount; i++) {
            if (stage.GetChildAt(i) is FrameLayout fl) {
                return fl;
            }
        }
        return null;
    }

    void ToggleVideoFullscreen() {
        if (fullscreenVideoDlg != null) {
            ExitVideoFullscreenIfNeeded();
            return;
        }
        if (musicVideoStage == null) {
            return;
        }

        double curSec = playerEngine?.CurrentSeconds ?? 0;
        musicVideoStage.PrepareSurfaceTransferSeek(curSec);
        (musicVideoStage.Parent as ViewGroup)?.RemoveView(musicVideoStage);

        FrameLayout fsRoot = new(this);
        fsRoot.SetBackgroundColor(Color.Black);
        fsRoot.AddView(musicVideoStage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        Android.App.Dialog dlg = new(this);
        dlg.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dlg.SetContentView(fsRoot);
        dlg.SetCancelable(true);
        if (dlg.Window != null) {
            dlg.Window.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Black));
            dlg.Window.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
                if (!OperatingSystem.IsAndroidVersionAtLeast(35)) {
#pragma warning disable CS0618, CA1422
                    dlg.Window.SetDecorFitsSystemWindows(false);
#pragma warning restore CS0618, CA1422
                }
                if (dlg.Window.Attributes != null) {
                    dlg.Window.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;
                    dlg.Window.Attributes = dlg.Window.Attributes;
                }
                View hudOverlay = FindFullscreenVideoHudOverlay(musicVideoStage);
                fsRoot.SetOnApplyWindowInsetsListener(new RootInsetsListener(insets => {
                    if (insets == null || hudOverlay == null || !OperatingSystem.IsAndroidVersionAtLeast(30)) {
                        return;
                    }
                    Android.Graphics.Insets safe = insets.GetInsets(
                        WindowInsets.Type.DisplayCutout() | WindowInsets.Type.SystemBars());
                    hudOverlay.SetPadding(safe.Left, safe.Top, safe.Right, safe.Bottom);
                }));
            } else {
#pragma warning disable CS0618, CA1422
                dlg.Window.AddFlags(WindowManagerFlags.Fullscreen);
#pragma warning restore CS0618, CA1422
            }
        }
        dlg.DismissEvent += (_, _) => ExitVideoFullscreenIfNeeded();

        fullscreenVideoDlg = dlg;
        SyncPredictiveBackRegistration();
        RequestedOrientation = ScreenOrientation.SensorLandscape;
        dlg.Show();
        dlg.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
            IWindowInsetsController ctrl = dlg.Window?.InsetsController;
            if (ctrl != null) {
                ctrl.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                ctrl.Hide(WindowInsets.Type.SystemBars());
            }
            fsRoot.RequestApplyInsets();
        }
        musicVideoStage.SetFullscreenMode(true);
    }

    void ExitVideoFullscreenIfNeeded() {
        Android.App.Dialog dlg = fullscreenVideoDlg;
        if (dlg == null) {
            return;
        }
        fullscreenVideoDlg = null;
        SyncPredictiveBackRegistration();

        try {
            double curSec = playerEngine?.CurrentSeconds ?? 0;
            musicVideoStage?.PrepareSurfaceTransferSeek(curSec);
            FindFullscreenVideoHudOverlay(musicVideoStage)?.SetPadding(0, 0, 0, 0);
            if (musicVideoStage?.Parent is ViewGroup parent) {
                parent.RemoveView(musicVideoStage);
            }
            if (musicVideoSlot != null && musicVideoStage != null) {
                musicVideoSlot.AddView(musicVideoStage, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
                musicVideoStage.SetFullscreenMode(false);
            }
        } catch {
            // Ignore view hierarchy cleanup races
        }

        try {
            if (dlg.IsShowing) {
                dlg.Dismiss();
            }
            dlg.Dispose();
        } catch {
            // Ignore
        }

        // Automatically rotate back to Portrait on fullscreen exit, then release sensor lock cleanly
        RequestedOrientation = ScreenOrientation.Portrait;
        uiHandler?.PostDelayed(() => {
            if (!uiDestroyed && fullscreenVideoDlg == null) {
                RequestedOrientation = ScreenOrientation.Unspecified;
            }
        }, 1400);
    }

    void ShowTrackSelectorDialog() {
        if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
            return;
        }
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
            CavernizeTrack tr = LoadedFile.Tracks[i];
            bool isSel = tr == selectedTrack;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = isSel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 80), 16f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
            OneUiTheme.AttachRecoil(row, 0.97f);

            TextView tv = Label($"#{i + 1}: {tr}", 13f, isSel ? ColText : OneUiTheme.TextSecondary);
            tv.SetTypeface(null, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (SelectedTrack != tr) {
                    SelectedTrack = tr;
                }
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        dlg = OneUiModalDialog.Show(
            this,
            S["SelectTrackHeader"],
            null,
            icon: OneUiIconType.Queue,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: list);
    }

    bool isSyncingSystemVolumeUi;
    int lastSyncedMasterVolPct = -1;
    bool lastSyncedDirectUsbVol;

    /// <summary>
    /// True ONLY when Path B direct libusb UAC2 is streaming (bypassing Android AudioFlinger's media volume mixer).
    /// All standard Android audio routes (speaker, headphones, Bluetooth, AAudio, AudioTrack) link directly to Stream.Music.
    /// </summary>
    bool IsDirectUsbSoftwareVolumeActive() =>
        audioOutput?.ProAudioDiagnostics.ActiveBackend == Cavernize.Logic.Spatial.ActiveProAudioBackendKind.LibUsbUac2Direct;

    int ReadSystemMediaVolumePercent(out int curIdx, out int maxIdx) {
        curIdx = 0;
        maxIdx = 15;
        try {
            if (GetSystemService(AudioService) is Android.Media.AudioManager am) {
                maxIdx = Math.Max(1, am.GetStreamMaxVolume(Android.Media.Stream.Music));
                curIdx = Math.Clamp(am.GetStreamVolume(Android.Media.Stream.Music), 0, maxIdx);
                return Math.Clamp((int)Math.Round(curIdx * 100.0 / maxIdx), 0, 100);
            }
        } catch {
            // Fallback outside AudioManager runtime
        }
        return Math.Clamp((int)Math.Round((store?.PlayerVolume ?? 1f) * 100f), 0, 100);
    }

    int GetCurrentMasterVolumePercent() =>
        IsDirectUsbSoftwareVolumeActive()
            ? Math.Clamp((int)Math.Round((store?.PlayerVolume ?? 1f) * 100f), 0, 100)
            : ReadSystemMediaVolumePercent(out _, out _);

    void SyncUiFromSystemMediaVolume(bool updateEngine = true) {
        if (uiDestroyed || isSyncingSystemVolumeUi) {
            return;
        }
        isSyncingSystemVolumeUi = true;
        try {
            bool isDirectUsb = IsDirectUsbSoftwareVolumeActive();
            int pct;
            if (isDirectUsb) {
                pct = Math.Clamp((int)Math.Round((store?.PlayerVolume ?? 1f) * 100f), 0, 100);
                if (updateEngine && playerEngine != null) {
                    playerEngine.Volume = pct / 100f;
                    playerEngine.SystemPlaybackVolume01 = 1f;
                }
            } else {
                pct = ReadSystemMediaVolumePercent(out int curIdx, out int maxIdx);
                float ratio01 = maxIdx > 0 ? Math.Clamp(curIdx / (float)maxIdx, 0f, 1f) : 1f;
                if (updateEngine && playerEngine != null) {
                    // Keep DSP master gain at unity 1.0f (zero double attenuation) while feeding real hardware ratio to ISO 226
                    playerEngine.Volume = 1.0f;
                    playerEngine.SystemPlaybackVolume01 = ratio01;
                }
            }

            bool uiMatches = pct == lastSyncedMasterVolPct &&
                             isDirectUsb == lastSyncedDirectUsbVol &&
                             (musicVolSeekBar == null || musicVolSeekBar.Progress == pct);
            if (uiMatches) {
                return;
            }
            lastSyncedMasterVolPct = pct;
            lastSyncedDirectUsbVol = isDirectUsb;

            if (musicVolSeekBar != null) {
                if (musicVolSeekBar.Max != 100) musicVolSeekBar.Max = 100;
                if (musicVolSeekBar.Progress != pct) {
                    musicVolSeekBar.Progress = pct;
                    musicVolSeekBar.Invalidate();
                }
            }
            if (roomMiniVolSeekBar != null) {
                if (roomMiniVolSeekBar.Max != 100) roomMiniVolSeekBar.Max = 100;
                if (roomMiniVolSeekBar.Progress != pct) {
                    roomMiniVolSeekBar.Progress = pct;
                    roomMiniVolSeekBar.Invalidate();
                }
            }
            if (roomMiniVolChip != null) {
                roomMiniVolChip.Text = string.Format(S["RoomMiniVolFmt"], pct);
            }
            RefreshMusicPlayerControlsUi();
        } finally {
            isSyncingSystemVolumeUi = false;
        }
    }

    void StepMasterVolumeByDelta(int deltaSteps) {
        if (deltaSteps == 0) {
            return;
        }
        if (IsDirectUsbSoftwareVolumeActive()) {
            int curPct = Math.Clamp((int)Math.Round((store?.PlayerVolume ?? 1f) * 100f), 0, 100);
            SetMasterVolumePercent(Math.Clamp(curPct + deltaSteps * 10, 0, 100));
            return;
        }
        try {
            if (GetSystemService(AudioService) is Android.Media.AudioManager am) {
                int maxIdx = Math.Max(1, am.GetStreamMaxVolume(Android.Media.Stream.Music));
                int curIdx = am.GetStreamVolume(Android.Media.Stream.Music);
                int nextIdx = Math.Clamp(curIdx + deltaSteps, 0, maxIdx);
                if (nextIdx != curIdx) {
                    am.SetStreamVolume(Android.Media.Stream.Music, nextIdx, (Android.Media.VolumeNotificationFlags)0);
                }
                SyncUiFromSystemMediaVolume(updateEngine: true);
                return;
            }
        } catch {
            // Fall back to percent step
        }
        SetMasterVolumePercent(Math.Clamp(ReadSystemMediaVolumePercent(out _, out _) + deltaSteps * 10, 0, 100));
    }

    void SetMasterVolumePercent(int percent) {
        if (isSyncingSystemVolumeUi) {
            return;
        }
        percent = Math.Clamp(percent, 0, 100);
        isSyncingSystemVolumeUi = true;
        try {
            bool isDirectUsb = IsDirectUsbSoftwareVolumeActive();
            if (isDirectUsb) {
                float vol = percent / 100f;
                store.PlayerVolume = vol;
                if (playerEngine != null) {
                    playerEngine.Volume = vol;
                    playerEngine.SystemPlaybackVolume01 = 1f;
                }
            } else {
                float ratio01 = percent / 100f;
                try {
                    if (GetSystemService(AudioService) is Android.Media.AudioManager am) {
                        int maxIdx = Math.Max(1, am.GetStreamMaxVolume(Android.Media.Stream.Music));
                        int curIdx = am.GetStreamVolume(Android.Media.Stream.Music);
                        int targetIdx = Math.Clamp((int)Math.Round(percent * maxIdx / 100.0), 0, maxIdx);
                        if (targetIdx != curIdx) {
                            am.SetStreamVolume(Android.Media.Stream.Music, targetIdx, (Android.Media.VolumeNotificationFlags)0);
                        }
                        ratio01 = Math.Clamp(targetIdx / (float)maxIdx, 0f, 1f);
                    }
                } catch {
                    // Best-effort system stream write
                }
                if (playerEngine != null) {
                    playerEngine.Volume = 1.0f;
                    playerEngine.SystemPlaybackVolume01 = ratio01;
                }
            }

            lastSyncedMasterVolPct = percent;
            lastSyncedDirectUsbVol = isDirectUsb;

            if (musicVolSeekBar != null && musicVolSeekBar.Progress != percent) {
                musicVolSeekBar.Progress = percent;
                musicVolSeekBar.Invalidate();
            }
            if (roomMiniVolSeekBar != null && roomMiniVolSeekBar.Progress != percent) {
                roomMiniVolSeekBar.Progress = percent;
                roomMiniVolSeekBar.Invalidate();
            }
            if (roomMiniVolChip != null) {
                roomMiniVolChip.Text = string.Format(S["RoomMiniVolFmt"], percent);
            }
            RefreshMusicPlayerControlsUi();
        } finally {
            isSyncingSystemVolumeUi = false;
        }
    }
}
