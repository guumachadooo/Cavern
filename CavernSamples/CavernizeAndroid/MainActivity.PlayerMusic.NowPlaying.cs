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
    void SetMusicPlayerSection(bool openLibrary) {
        isLibrarySectionOpen = openLibrary;
        if (openLibrary && isFormatPillExpanded) {
            SetFormatPillExpanded(false, animate: false);
        }
        ApplyCurrentWindowInsetsPadding();
        UpdateSectionSwitcherStyle();
        SyncPredictiveBackRegistration();

        // Show full bottom nav bar when browsing Library; hide it on Now Playing (integrated '+' pill handles navigation)
        if (activeTab == 0) {
            SetBottomNavBarVisible(openLibrary, animate: true);
        }

        if (musicNowPlayingScrollView != null && musicLibraryScrollView != null) {
            View entering = openLibrary ? (View)musicLibraryScrollView : musicNowPlayingScrollView;
            View leaving = openLibrary ? (View)musicNowPlayingScrollView : musicLibraryScrollView;

            leaving.Animate()?.Cancel();
            leaving.Visibility = ViewStates.Gone;
            leaving.Alpha = 1f;
            leaving.TranslationX = 0f;

            entering.Visibility = ViewStates.Visible;
            entering.Alpha = 0f;
            entering.TranslationX = openLibrary ? Dp(18) : -Dp(18);
            entering.Animate()
                .Alpha(1f)
                .TranslationX(0f)
                .SetDuration(260)
                .SetInterpolator(CreateOneUiEase())
                .Start();
        }

        if (openLibrary) {
            musicVideoStage?.PauseVideoDecoderOnly();
            if (librarySnapshot == null || IsLibraryBaseIndexStale()) {
                ScheduleLibraryLoadAndRenderAsync(loadCacheFromDisk: librarySnapshot == null);
            } else {
                RenderLibraryView();
            }
            if (!isLibraryScanning) {
                RefreshLibraryScanAsync(force: false);
            }
        } else if (musicStageMode == PlayerStageMode.Video && activeTab == 0) {
            musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
        }
    }

    void UpdateSectionSwitcherStyle() {
        if (segNowPlayingBtn == null || segLibraryBtn == null) return;
        void Style(TextView tv, bool selected) {
            tv.Background = selected
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(228, 252, 252, 255), 999f)
                : null;
            tv.SetTextColor(selected ? Color.Rgb(18, 20, 26) : Color.Argb(210, 240, 242, 248));
        }
        Style(segNowPlayingBtn, !isLibrarySectionOpen);
        Style(segLibraryBtn, isLibrarySectionOpen);
    }

    void SetPlayerStageMode(PlayerStageMode mode, bool animate = true) {
        if (currentMediaMeta?.HasVideo == true) {
            mode = PlayerStageMode.Video;
        } else if (mode == PlayerStageMode.Video) {
            mode = PlayerStageMode.Cover;
        }
        PlayerStageMode prevMode = musicStageMode;
        musicStageMode = mode;
        SyncPredictiveBackRegistration();

        if (mode == PlayerStageMode.Lyrics && isFormatPillExpanded) {
            SetFormatPillExpanded(false, animate: false);
        }

        bool canAnimateCoverLyrics = animate &&
            prevMode != mode &&
            musicArtworkSlot != null &&
            musicLyricsContainer != null &&
            ((prevMode == PlayerStageMode.Cover && mode == PlayerStageMode.Lyrics) ||
             (prevMode == PlayerStageMode.Lyrics && mode == PlayerStageMode.Cover));

        int myAnimGen = ++stageAnimGen;
        musicArtworkSlot?.Animate()?.Cancel();
        musicLyricsContainer?.Animate()?.Cancel();

        if (musicVideoSlot != null) {
            musicVideoSlot.Visibility = mode == PlayerStageMode.Video ? ViewStates.Visible : ViewStates.Gone;
            if (mode == PlayerStageMode.Video && activeTab == 0 && !isLibrarySectionOpen) {
                musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
            } else {
                musicVideoStage?.PauseVideoDecoderOnly();
            }
        }

        // Simplified Lyrics Mode: hide non-essential rows so Lyrics expands smoothly across the entire viewport
        ViewStates chromeVis = mode == PlayerStageMode.Lyrics ? ViewStates.Gone : ViewStates.Visible;
        if (canAnimateCoverLyrics && musicNowPlayingSection != null) {
            try {
                Android.Transitions.TransitionSet ts = new();
                ts.SetOrdering(Android.Transitions.TransitionOrdering.Together);
                Android.Transitions.ChangeBounds cb = new();
                cb.SetDuration(340);
                Android.Transitions.Fade fd = new();
                fd.SetDuration(220);
                ts.AddTransition(cb);
                ts.AddTransition(fd);
                ts.SetInterpolator(CreateOneUiEase());
                Android.Transitions.TransitionManager.BeginDelayedTransition(musicNowPlayingSection, ts);
            } catch { }
        }

        if (musicTitleRow != null) {
            musicTitleRow.Visibility = chromeVis;
            musicTitleRow.Alpha = 1f;
        }
        if (badgeFormatRow != null) {
            badgeFormatRow.Visibility = chromeVis;
            badgeFormatRow.Alpha = 1f;
        }
        if (musicVolRow != null) {
            musicVolRow.Visibility = chromeVis;
            musicVolRow.Alpha = 1f;
        }
        if (musicSpatialToggleRow != null) {
            musicSpatialToggleRow.Visibility = chromeVis;
            musicSpatialToggleRow.Alpha = 1f;
        }

        if (canAnimateCoverLyrics) {
            bool toLyrics = mode == PlayerStageMode.Lyrics;
            View outgoing = toLyrics ? (View)musicArtworkSlot : musicLyricsContainer;
            View incoming = toLyrics ? (View)musicLyricsContainer : musicArtworkSlot;

            if (toLyrics) {
                musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
            }

            outgoing.Visibility = ViewStates.Visible;
            outgoing.Animate()
                .Alpha(0f)
                .ScaleX(0.95f)
                .ScaleY(0.95f)
                .TranslationY(toLyrics ? -Dp(12) : Dp(12))
                .SetDuration(190)
                .SetInterpolator(CreateOneUiEase())
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myAnimGen != stageAnimGen) return;
                    outgoing.Visibility = ViewStates.Gone;
                    outgoing.Alpha = 1f;
                    outgoing.ScaleX = 1f;
                    outgoing.ScaleY = 1f;
                    outgoing.TranslationY = 0f;
                }))
                .Start();

            incoming.Visibility = ViewStates.Visible;
            incoming.Alpha = 0f;
            incoming.ScaleX = 0.96f;
            incoming.ScaleY = 0.96f;
            incoming.TranslationY = toLyrics ? Dp(16) : -Dp(16);
            incoming.Animate()
                .Alpha(1f)
                .ScaleX(1f)
                .ScaleY(1f)
                .TranslationY(0f)
                .SetStartDelay(45)
                .SetDuration(320)
                .SetInterpolator(new Android.Views.Animations.PathInterpolator(0.16f, 1f, 0.3f, 1f))
                .Start();
        } else {
            if (musicArtworkSlot != null) {
                musicArtworkSlot.Visibility = mode == PlayerStageMode.Cover ? ViewStates.Visible : ViewStates.Gone;
                musicArtworkSlot.Alpha = 1f;
                musicArtworkSlot.ScaleX = 1f;
                musicArtworkSlot.ScaleY = 1f;
                musicArtworkSlot.TranslationY = 0f;
            }
            if (musicLyricsContainer != null) {
                musicLyricsContainer.Visibility = mode == PlayerStageMode.Lyrics ? ViewStates.Visible : ViewStates.Gone;
                musicLyricsContainer.Alpha = 1f;
                musicLyricsContainer.ScaleX = 1f;
                musicLyricsContainer.ScaleY = 1f;
                musicLyricsContainer.TranslationY = 0f;
                if (mode == PlayerStageMode.Lyrics) {
                    musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
                }
            }
        }

        RefreshLyricsSingDockUi();
        RefreshBottomActionBarVisuals();
        musicTabRootPage?.Post(() => UpdatePlayTabSpacingForViewport(force: true));
    }

    void ApplyLyricsSingVocalLevel(int pct) {
        int clamped = Math.Clamp(pct, 0, 100);
        float level01 = clamped * 0.01f;
        store.KaraokeVocalLevel = level01;
        bool shouldEnableKaraoke = clamped < 99;
        if (store.SpatialKaraokeEnabled != shouldEnableKaraoke) {
            store.SpatialKaraokeEnabled = shouldEnableKaraoke;
            playerEngine?.ApplyNeuralConfig(store.BuildNeuralAudioConfig());
        } else {
            playerEngine?.SetKaraokeVocalLevel(level01);
        }
        if (karaokeVocalLevelBar != null) {
            karaokeVocalLevelBar.Progress = clamped;
            karaokeVocalLevelBar.Invalidate();
        }
        if (spatialKaraokeSwitch != null && spatialKaraokeSwitch.Checked != store.SpatialKaraokeEnabled) {
            spatialKaraokeSwitch.Checked = store.SpatialKaraokeEnabled;
        }
        RefreshLyricsSingDockUi();
        RefreshStudioW12ContextUi();
    }

    string lastLyricsSingDockSig = "";

    void RefreshLyricsSingDockUi() {
        if (musicLyricsSingDock == null || store == null) {
            return;
        }
        int pct = Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100);
        bool micEnabled = store.LiveMicEnabled;
        bool dryBypass = playerEngine?.VocalChainBypassed == true;
        bool karaokeOn = store.SpatialKaraokeEnabled || pct < 99;
        bool hasUsb = usbProAudioManager?.CurrentSnapshot?.IsAttached == true;
        bool hasDna = playerEngine?.VocalMixMatchStage?.ActiveDna?.IsExtractedFromSong == true;
        bool hasVp = playerEngine?.VocalMixMatchStage?.HasMicVoiceprint == true;
        bool hasNeuralLead = playerEngine?.NeuralStage?.HasIsolatedNeuralVocalReference == true;
        bool prepRunning = isNeuralPrepRunning;

        string sig = $"{isLyricsSingDockExpanded}|{pct}|{micEnabled}|{dryBypass}|{karaokeOn}|{hasUsb}|{hasDna}|{hasVp}|{hasNeuralLead}|{prepRunning}";
        if (string.Equals(lastLyricsSingDockSig, sig, StringComparison.Ordinal)) {
            return;
        }
        lastLyricsSingDockSig = sig;

        ViewStates expectedDockVis = isLyricsSingDockExpanded ? ViewStates.Visible : ViewStates.Gone;
        if (musicLyricsSingDock.Visibility != expectedDockVis) {
            musicLyricsSingDock.Visibility = expectedDockVis;
        }
        if (musicLyricsSingPctText != null) {
            musicLyricsSingPctText.Text = $"{pct}%";
            musicLyricsSingPctText.SetTextColor(pct < 99 ? OneUiTheme.IconSystemInfo : OneUiTheme.PrimaryHigh);
        }
        if (musicLyricsVocalSeekBar != null && musicLyricsVocalSeekBar.Progress != pct) {
            musicLyricsVocalSeekBar.Progress = pct;
            musicLyricsVocalSeekBar.Invalidate();
        }
        if (musicLyricsSingMicIcon != null) {
            musicLyricsSingMicIcon.IconColor = micEnabled
                ? OneUiTheme.IconSystemInfo
                : (pct < 99 ? OneUiTheme.PrimaryHigh : Color.Argb(215, 252, 252, 255));
        }
        if (musicLyricsSingToggleBtn != null) {
            bool anySingActive = micEnabled || karaokeOn;
            Color toggleBg = isLyricsSingDockExpanded
                ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 108)
                : (anySingActive
                    ? OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 68)
                    : Color.Argb(38, 255, 255, 255));
            Color toggleStroke = isLyricsSingDockExpanded
                ? OneUiTheme.WithAlpha(OneUiTheme.PrimaryHigh, 200)
                : (anySingActive
                    ? OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 175)
                    : Color.Argb(58, 255, 255, 255));
            Color toggleFg = isLyricsSingDockExpanded
                ? OneUiTheme.PrimaryHigh
                : (anySingActive ? OneUiTheme.IconSystemInfo : Color.Argb(230, 252, 252, 255));
            musicLyricsSingToggleBtn.Background = OneUiTheme.CreateRoundedDrawable(this, toggleBg, 99f, toggleStroke, isLyricsSingDockExpanded ? 1.1f : 0.95f);
            if (musicLyricsSingToggleIcon != null) {
                musicLyricsSingToggleIcon.IconColor = toggleFg;
            }
            if (musicLyricsSingToggleText != null) {
                musicLyricsSingToggleText.SetTextColor(toggleFg);
            }
        }
        if (musicLyricsKaraokeBtn != null) {
            musicLyricsKaraokeBtn.Text = S["SingDockKaraokeChip"];
            musicLyricsKaraokeBtn.Background = karaokeOn
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 86), 99f, OneUiTheme.WithAlpha(OneUiTheme.PrimaryHigh, 185), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(48, 255, 255, 255), 0.9f);
        }
        if (musicLyricsLiveMicBtn != null) {
            musicLyricsLiveMicBtn.Text = micEnabled ? S["SingDockLiveMicOnChip"] : S["SingDockLiveMicChip"];
            musicLyricsLiveMicBtn.Background = micEnabled
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 175), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(48, 255, 255, 255), 0.9f);
        }
        if (musicLyricsMixMatchBtn != null) {
            bool micLive = micEnabled && !dryBypass;
            musicLyricsMixMatchBtn.Text = micEnabled
                ? (dryBypass ? S["SingDockMixMatchDryChip"] : S["SingDockMixMatchWetChip"])
                : S["SingDockMixMatchChip"];
            musicLyricsMixMatchBtn.Background = micLive
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 170), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(38, 255, 255, 255), 99f, Color.Argb(54, 255, 255, 255), 0.9f);
        }
        if (musicLyricsVocalAbBtn != null) {
            musicLyricsVocalAbBtn.Visibility = micEnabled ? ViewStates.Visible : ViewStates.Gone;
            if (micEnabled) {
                musicLyricsVocalAbBtn.Text = dryBypass ? S["SingDockVocalAbDryChip"] : S["SingDockVocalAbWetChip"];
                musicLyricsVocalAbBtn.Background = dryBypass
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 82), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 185), 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 175), 1f);
            }
        }
        if (musicLyricsScarlettBtn != null) {
            musicLyricsScarlettBtn.Background = hasUsb
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 175), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(48, 255, 255, 255), 0.9f);
        }
        if (isLyricsSingDockExpanded || prepRunning) {
            RefreshNeuralPrepUi();
        }
        if (musicLyricsAiStatusText != null) {
            if (!prepRunning) {
                if (karaokeOn && pct < 99) {
                    string modeBadge = hasNeuralLead ? S["SingDockKaraokeAiPill"] : S["SingDockKaraokeCenterPill"];
                    musicLyricsAiStatusText.Text = (hasDna || hasVp) ? $"{modeBadge} · {S["SingDockDnaActivePill"]}" : modeBadge;
                    musicLyricsAiStatusText.SetTextColor(hasNeuralLead ? OneUiTheme.FunctionalGreen : OneUiTheme.IconSystemInfo);
                } else {
                    musicLyricsAiStatusText.Text = (hasDna || hasVp) ? S["SingDockDnaActivePill"] : S["SingDockDnaIdlePill"];
                    musicLyricsAiStatusText.SetTextColor((hasDna || hasVp) ? OneUiTheme.IconSystemInfo : OneUiTheme.TextSecondary);
                }
            }
            if (musicLyricsAiStatusText.Visibility != ViewStates.Visible) {
                musicLyricsAiStatusText.Visibility = ViewStates.Visible;
            }
            Color pillBg = prepRunning
                ? OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 48)
                : (hasNeuralLead && karaokeOn && pct < 99
                    ? OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 44)
                    : (hasDna || hasVp || micEnabled || (karaokeOn && pct < 99)
                        ? OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 44)
                        : Color.Argb(34, 255, 255, 255)));
            musicLyricsAiStatusText.Background = OneUiTheme.CreateRoundedDrawable(
                this, pillBg, 99f, Color.Argb(54, 255, 255, 255), 0.8f);
        }
    }

    void RefreshBottomActionBarVisuals() {
        if (musicBottomLeftBtn == null) return;

        bool hasVideo = currentMediaMeta?.HasVideo == true;
        if (hasVideo) {
            musicBottomLeftIcon.IconType = OneUiIconType.SubtitlesCc;
            bool subActive = activeSubtitleCues.Count > 0;
            musicBottomLeftBtn.Background = subActive
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(225, 252, 252, 255), 14f)
                : null;
            musicBottomLeftIcon.IconColor = subActive ? Color.Rgb(20, 22, 28) : Color.Argb(195, 252, 252, 255);
        } else {
            musicBottomLeftIcon.IconType = OneUiIconType.LyricsQuote;
            bool lyricsActive = musicStageMode == PlayerStageMode.Lyrics;
            musicBottomLeftBtn.Background = lyricsActive
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(225, 252, 252, 255), 14f)
                : null;
            musicBottomLeftIcon.IconColor = lyricsActive ? Color.Rgb(20, 22, 28) : Color.Argb(195, 252, 252, 255);
        }

        // Flanking spatial toggles: [Cavernize 3D] | [HRTF 3D / Direto] | [Matrix 7.1]
        void StyleSpatialPill(LinearLayout btn, OneUiIconView ico, TextView tv, bool on, Color accent, string labelText = null) {
            if (btn == null) return;
            if (labelText != null) tv.Text = labelText;
            btn.Background = on
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(accent, 96), 99f, OneUiTheme.WithAlpha(accent, 175), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(24, 255, 255, 255), 99f, Color.Argb(26, 255, 255, 255), 0.8f);
            Color fg = on ? OneUiTheme.TextPrimary : Color.Argb(150, 240, 242, 250);
            ico.IconColor = on ? Color.White : fg;
            tv.SetTextColor(fg);
        }

        StyleSpatialPill(musicCavernizeToggleBtn, musicCavernizeIcon, musicCavernizeText, store.CavernizeUpmix, OneUiTheme.IconAtmosUpmix);
        StyleSpatialPill(musicBottomSpatialBtn, musicBottomSpatialIcon, musicBottomSpatialText, store.HeadphoneVirtualizer, OneUiTheme.Primary,
            BinauralProfileService.GetActiveShortBadge(store, S));
        StyleSpatialPill(musicMatrixToggleBtn, musicMatrixIcon, musicMatrixText, store.MatrixUpmixing, OneUiTheme.IconStudioExport);

        bool loop = playerEngine?.Loop ?? false;
        musicBottomLoopBtn.Background = loop
            ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(52, 255, 255, 255), 14f)
            : null;
        musicBottomLoopIcon.IconColor = loop ? OneUiTheme.TextPrimary : Color.Argb(180, 252, 252, 255);
    }

    static bool IsBitmapUsable(Bitmap bmp) {
        if (bmp == null) return false;
        try {
            return bmp.Handle != IntPtr.Zero && !bmp.IsRecycled && bmp.Width > 0 && bmp.Height > 0;
        } catch {
            return false;
        }
    }

    void RefreshMusicPlayerUi() {
        if (musicArtworkView == null) {
            return;
        }
        Color accent = currentMediaMeta?.PrimaryAccent ?? OneUiTheme.Primary;
        Color accent2 = currentMediaMeta?.SecondaryAccent ?? OneUiTheme.Secondary;
        backdropView?.SetMediaAccent(
            LoadedFile != null ? accent : null,
            LoadedFile != null ? accent2 : null);

        Bitmap activeArt = (LoadedFile != null && IsBitmapUsable(currentMediaMeta?.ArtworkBitmap))
            ? currentMediaMeta.ArtworkBitmap
            : (defaultFallbackCover ??= MediaMetadataHelper.CreateSpatialFallbackArtwork("Cavernize", OneUiTheme.PrimaryHigh, OneUiTheme.Secondary, 512));
        musicArtworkView.SetArtwork(activeArt, LoadedFile != null ? accent : OneUiTheme.Primary);
        musicLyricsMiniArt?.SetArtwork(activeArt, LoadedFile != null ? accent : OneUiTheme.Primary);

        bool hasVideo = LoadedFile != null && currentMediaMeta?.HasVideo == true && !string.IsNullOrEmpty(currentMediaMeta.SourceMediaPath);
        if (hasVideo) {
            musicVideoStage?.BindVideoSource(currentMediaMeta.SourceMediaPath, currentMediaMeta.VideoWidth, currentMediaMeta.VideoHeight);
        } else {
            ExitVideoFullscreenIfNeeded();
            musicVideoStage?.ReleasePlayer();
        }

        musicLyricsView?.SetLyrics(currentMediaMeta?.Lyrics, S["NoLyricsFound"], accent);

        CavernizeTrack active = GetActiveTrack();
        string badgeStr;
        if (LoadedFile == null || active == null) {
            musicTitleText.Text = S["PlayerEmptyTitle"];
            musicArtistText.Text = S["PlayerEmptySub"];
            if (musicLyricsHeaderTitle != null) musicLyricsHeaderTitle.Text = S["PlayerEmptyTitle"];
            if (musicLyricsHeaderSub != null) musicLyricsHeaderSub.Text = S["ModeLyrics"];
            badgeStr = S["PlayerBadgeSpatial"];
            musicSpatialBadgeText.Text = badgeStr;
            musicWaveSeekBar?.SetThemeByColor(OneUiTheme.Tertiary);
        } else {
            string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                ? currentMediaMeta.Title
                : Path.GetFileNameWithoutExtension(LoadedFile.Path);
            musicTitleText.Text = title;

            string sub = string.Join(" — ", new[] { currentMediaMeta?.Artist, currentMediaMeta?.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
            musicArtistText.Text = !string.IsNullOrWhiteSpace(sub)
                ? sub
                : active.FormatHeader;

            if (musicLyricsHeaderTitle != null) {
                musicLyricsHeaderTitle.Text = title;
            }
            if (musicLyricsHeaderSub != null) {
                musicLyricsHeaderSub.Text = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist)
                    ? currentMediaMeta.Artist
                    : active.FormatHeader;
            }

            badgeStr = BuildSpatialBadgeSummary(active);
            UpdateSpatialBadgePillVisuals(badgeStr);

            bool hasArt = currentMediaMeta?.HasEmbeddedArtwork == true;
            Color sliderTone = hasArt ? accent : OneUiTheme.Tertiary;
            musicWaveSeekBar?.SetThemeByColor(sliderTone);
        }

        if (hasVideo) {
            string trkStr = LoadedFile.Tracks.Count > 1
                ? $"{Math.Max(1, LoadedFile.Tracks.ToList().IndexOf(selectedTrack) + 1)}/{LoadedFile.Tracks.Count}"
                : "";
            musicVideoStage?.ConfigureMetadata(
                musicTitleText.Text,
                badgeStr,
                trkStr,
                LoadedFile.Tracks.Count > 1,
                activeSubtitleCues.Count > 0,
                playerEngine?.Loop ?? false,
                S["VideoUnlockHint"]);
        }

        string boundKey = LoadedFile != null ? (currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "") : "";
        if (!string.Equals(lastStageBoundPath, boundKey, StringComparison.Ordinal)) {
            lastStageBoundPath = boundKey;
            SetFormatPillExpanded(false, animate: false);
            SetPlayerStageMode(hasVideo ? PlayerStageMode.Video : PlayerStageMode.Cover, animate: false);
        } else {
            SetPlayerStageMode(musicStageMode, animate: false);
        }

        RefreshActiveTrackFormatSwitcher(triggerDeepAnalysis: true);
        RefreshLyricsSingDockUi();
        RefreshMusicPlayerControlsUi();
    }

    string BuildSpatialBadgeSummary(CavernizeTrack active) {
        if (LoadedFile == null || active == null) {
            return S["PlayerBadgeSpatial"];
        }
        int objs = trackSourceItems.Count(x => x.IsDynamicObject);
        Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        bool twinSynced = lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
        bool lgsrBypassed = playerEngine?.LgsrBypassed ?? false;

        string badgeStr;
        if (twinSynced) {
            string twinTag = lgsrBypassed ? S["AudioSpecAtmosAbBadge"] : S["AudioSpecAtmosFlacBadge"];
            badgeStr = objs > 0 ? string.Format(S["PlayerBadgeObjFmt"], twinTag, objs) : twinTag;
        } else if (active.SourceCodec == Codec.TrueHD) {
            badgeStr = objs > 0
                ? string.Format(S["PlayerBadgeTrueHdAtmosObjs"], objs)
                : S["PlayerBadgeTrueHdLossless"];
        } else {
            badgeStr = objs > 0
                ? string.Format(S["PlayerBadgeSpatialObjs"], objs)
                : active.Codec.ToString().ToUpperInvariant();
        }
        if (activeLibraryQueue.Count > 1 && activeLibraryQueueIndex >= 0) {
            badgeStr = $"{badgeStr} • {activeLibraryQueueIndex + 1}/{activeLibraryQueue.Count}";
        }
        return badgeStr;
    }

    void UpdateSpatialBadgePillVisuals(string badgeOverride = null) {
        if (musicSpatialBadgeText == null || musicSpatialBadgePill == null) {
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        string badgeStr = badgeOverride ?? BuildSpatialBadgeSummary(active);
        musicSpatialBadgeText.Text = badgeStr;

        Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        bool twinSynced = LoadedFile != null && lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
        bool lgsrBypassed = playerEngine?.LgsrBypassed ?? false;

        if (twinSynced && !lgsrBypassed) {
            musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 56), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 165), 1f);
            if (musicSpatialBadgeIcon != null) {
                musicSpatialBadgeIcon.IconColor = OneUiTheme.IconSystemInfo;
            }
        } else if (twinSynced && lgsrBypassed) {
            musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 48), 99f, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 145), 1f);
            if (musicSpatialBadgeIcon != null) {
                musicSpatialBadgeIcon.IconColor = OneUiTheme.PeachText;
            }
        } else {
            musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(
                this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(38, 255, 255, 255), 0.8f);
            if (musicSpatialBadgeIcon != null) {
                musicSpatialBadgeIcon.IconColor = Color.Argb(228, 252, 252, 255);
            }
        }
        ApplyFormatPillExpandFraction(isFormatPillExpanded ? 1f : 0f);
    }

    void RefreshActiveTrackFormatSwitcher(bool triggerDeepAnalysis = true) {
        if (musicFormatSwitchRow == null || musicFormatPillContainer == null) return;
        musicFormatSwitchRow.RemoveAllViews();
        currentTrackVariants.Clear();

        if (LoadedFile == null) {
            musicFormatPillContainer.Visibility = ViewStates.Gone;
            SetFormatPillExpanded(false, animate: false);
            return;
        }
        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        if (snap == null) {
            musicFormatPillContainer.Visibility = ViewStates.Gone;
            SetFormatPillExpanded(false, animate: false);
            return;
        }
        string curPath = currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "";
        List<LibraryTrackItem> variants = MediaLibraryService.FindVariantsForTrack(
            snap, curPath, currentMediaMeta?.Title ?? "", currentMediaMeta?.Artist ?? "", currentMediaMeta?.Album ?? "");
        if (variants.Count <= 1) {
            musicFormatPillContainer.Visibility = ViewStates.Gone;
            SetFormatPillExpanded(false, animate: false);
            return;
        }

        // Strict tier ordering (Atmos -> Surround -> Stereo) + scientific quality ranking inside each tier
        AudioQualityAnalyzer.SortVariantsIntelligently(variants, this);
        currentTrackVariants.AddRange(variants);
        musicFormatPillContainer.Visibility = ViewStates.Visible;

        RebuildFormatPillChips();
        ApplyFormatPillExpandFraction(isFormatPillExpanded ? 1f : 0f);

        if (triggerDeepAnalysis) {
            string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
            AudioQualityAnalyzer.AnalyzeVariantsInBackground(this, variants, ffLoc, () => {
                RunOnUiThread(() => {
                    if (!uiDestroyed) {
                        RefreshActiveTrackFormatSwitcher(triggerDeepAnalysis: false);
                    }
                });
            });
        }
    }

    void RebuildFormatPillChips(bool? forceExpandedChips = null) {
        if (musicFormatSwitchRow == null || currentTrackVariants.Count <= 1 || LoadedFile == null) {
            return;
        }
        bool showExpanded = forceExpandedChips ?? isFormatPillExpanded;
        isFormatPillShowingExpandedChips = showExpanded;
        musicFormatSwitchRow.RemoveAllViews();
        string curPath = currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "";
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);

        int activeIdx = 0;
        for (int i = 0; i < currentTrackVariants.Count; i++) {
            LibraryTrackItem v = currentTrackVariants[i];
            if (string.Equals(v.FilePath, curPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.FilePath, LoadedFile.Path, StringComparison.OrdinalIgnoreCase)) {
                activeIdx = i;
                break;
            }
        }

        if (!showExpanded) {
            // Compact collapsed pill (26dp total height; chips fill exact 22dp inner height for mathematical centering)
            LibraryTrackItem activeVar = currentTrackVariants[activeIdx];
            string activeLabel = MediaLibraryService.GetDistinctVariantLabel(activeVar, currentTrackVariants);

            TextView activeChip = new(this) {
                Text = activeLabel,
                TextSize = 10f,
                Gravity = GravityFlags.Center
            };
            activeChip.SetTypeface(sfBold, TypefaceStyle.Bold);
            activeChip.SetIncludeFontPadding(false);
            activeChip.SetSingleLine(true);
            activeChip.Ellipsize = TextUtils.TruncateAt.End;
            activeChip.SetPadding(Dp(8), 0, Dp(8), 0);
            activeChip.SetTextColor(Color.Rgb(18, 20, 26));
            activeChip.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(240, 252, 252, 255), 99f);

            TextView moreCountTv = new(this) {
                Text = $"+{currentTrackVariants.Count - 1}",
                TextSize = 9.8f,
                Gravity = GravityFlags.Center
            };
            moreCountTv.SetTypeface(sfBold, TypefaceStyle.Bold);
            moreCountTv.SetIncludeFontPadding(false);
            moreCountTv.SetSingleLine(true);
            moreCountTv.SetPadding(Dp(5), 0, Dp(6), 0);
            moreCountTv.SetTextColor(Color.Argb(232, 245, 248, 255));

            musicFormatSwitchRow.AddView(activeChip, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
            musicFormatSwitchRow.AddView(moreCountTv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));

            void ExpandHandler(object s, EventArgs e) => SetFormatPillExpanded(true, animate: true);
            activeChip.Click += ExpandHandler;
            moreCountTv.Click += ExpandHandler;

            musicFormatSwitchRow.Measure(
                View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
                View.MeasureSpec.MakeMeasureSpec(Dp(26), MeasureSpecMode.Exactly));
            formatCollapsedWidthPx = Math.Max(Dp(54), musicFormatSwitchRow.MeasuredWidth);
            return;
        }

        // Expanded mode: show all variants preserving active variant's relative index + 3 bespoke category vector logos
        bool hasMultipleCategories = currentTrackVariants
            .Select(AudioQualityAnalyzer.ClassifyCategory)
            .Distinct()
            .Count() > 1;

        AudioTierCategory? lastCategory = null;
        View activeChipView = null;

        for (int i = 0; i < currentTrackVariants.Count; i++) {
            LibraryTrackItem v = currentTrackVariants[i];
            AudioTierCategory cat = AudioQualityAnalyzer.ClassifyCategory(v);

            if (hasMultipleCategories && lastCategory != cat) {
                if (lastCategory.HasValue) {
                    View divider = new(this) {
                        Background = new Android.Graphics.Drawables.ColorDrawable(Color.Argb(54, 255, 255, 255)),
                        Tag = "extra"
                    };
                    musicFormatSwitchRow.AddView(divider, new LinearLayout.LayoutParams(Dp(1), Dp(12)) {
                        Gravity = GravityFlags.CenterVertical,
                        LeftMargin = Dp(4),
                        RightMargin = Dp(3)
                    });
                }

                OneUiIconType logoIcon = cat switch {
                    AudioTierCategory.Atmos => OneUiIconType.AudioLogoAtmos,
                    AudioTierCategory.Surround => OneUiIconType.AudioLogoSurround,
                    _ => OneUiIconType.AudioLogoStereo
                };
                Color logoColor = cat switch {
                    AudioTierCategory.Atmos => OneUiTheme.PrimaryHigh,
                    AudioTierCategory.Surround => OneUiTheme.PeachText,
                    _ => Color.Argb(204, 232, 236, 245)
                };
                OneUiIconView catLogo = new(this, logoIcon, logoColor) {
                    Tag = "extra"
                };
                musicFormatSwitchRow.AddView(catLogo, new LinearLayout.LayoutParams(Dp(14), Dp(14)) {
                    Gravity = GravityFlags.CenterVertical,
                    LeftMargin = Dp(4),
                    RightMargin = Dp(3)
                });
                lastCategory = cat;
            }

            bool isActive = i == activeIdx;
            string lbl = MediaLibraryService.GetDistinctVariantLabel(v, currentTrackVariants);

            TextView chip = new(this) {
                Text = lbl,
                TextSize = 10f,
                Gravity = GravityFlags.Center,
                Tag = isActive ? "active" : "extra"
            };
            chip.SetTypeface(sfBold, TypefaceStyle.Bold);
            chip.SetIncludeFontPadding(false);
            chip.SetSingleLine(true);
            chip.Ellipsize = TextUtils.TruncateAt.End;
            chip.SetPadding(Dp(8), 0, Dp(8), 0);
            Color inactiveFg = cat switch {
                AudioTierCategory.Atmos => OneUiTheme.PrimaryHigh,
                AudioTierCategory.Surround => OneUiTheme.PeachText,
                _ => Color.Argb(224, 240, 242, 248)
            };
            chip.SetTextColor(isActive ? Color.Rgb(18, 20, 26) : inactiveFg);
            chip.Background = isActive
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(240, 252, 252, 255), 99f)
                : null;
            OneUiTheme.AttachRecoil(chip, 0.92f);

            LibraryTrackItem capturedVar = v;
            bool capturedActive = isActive;
            chip.Click += (_, _) => {
                SetFormatPillExpanded(false, animate: true);
                if (!capturedActive) {
                    SwitchActiveTrackVariant(capturedVar, currentTrackVariants);
                }
            };
            musicFormatSwitchRow.AddView(chip, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.MatchParent) {
                RightMargin = i < currentTrackVariants.Count - 1 ? Dp(2) : 0
            });
            if (isActive) {
                activeChipView = chip;
            }
        }

        musicFormatSwitchRow.Measure(
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
            View.MeasureSpec.MakeMeasureSpec(Dp(26), MeasureSpecMode.Exactly));
        formatExpandedWidthPx = Math.Max(formatCollapsedWidthPx, musicFormatSwitchRow.MeasuredWidth);

        if (activeChipView != null) {
            musicFormatScrollView?.Post(() => {
                if (uiDestroyed || musicFormatScrollView == null) return;
                int scrollX = Math.Max(0, activeChipView.Left - (musicFormatScrollView.Width - activeChipView.Width) / 2);
                musicFormatScrollView.SmoothScrollTo(scrollX, 0);
            });
        }
    }

    void SetFormatPillExpanded(bool expanded, bool animate) {
        if (musicSpatialBadgePill == null || musicFormatPillContainer == null) {
            isFormatPillExpanded = false;
            return;
        }
        bool wasExpanded = isFormatPillExpanded;
        isFormatPillExpanded = expanded;
        formatPillAnimator?.Cancel();

        float target = expanded ? 1f : 0f;
        if (!animate || currentTrackVariants.Count <= 1) {
            RebuildFormatPillChips(forceExpandedChips: expanded);
            formatExpandFraction = target;
            ApplyFormatPillExpandFraction(target);
            return;
        }

        if (expanded) {
            // Capture exact collapsed width before swapping to expanded chips so the pill morphs from collapsedW -> expandedW with zero jump
            if (!isFormatPillShowingExpandedChips && musicFormatPillContainer.Width > Dp(40)) {
                formatCollapsedWidthPx = musicFormatPillContainer.Width;
            }
            RebuildFormatPillChips(forceExpandedChips: true);
        } else if (!wasExpanded && !isFormatPillShowingExpandedChips) {
            RebuildFormatPillChips(forceExpandedChips: false);
            formatExpandFraction = 0f;
            ApplyFormatPillExpandFraction(0f);
            return;
        }

        float startF = formatExpandFraction;
        bool targetExpanded = expanded;
        formatPillAnimator = ValueAnimator.OfFloat(startF, target);
        formatPillAnimator.SetDuration(expanded ? 320 : 280);
        formatPillAnimator.SetInterpolator(CreateOneUiEase());
        formatPillAnimator.Update += (_, e) => {
            float f = (float)(e.Animation?.AnimatedValue ?? target);
            formatExpandFraction = f;
            ApplyFormatPillExpandFraction(f);
        };
        formatPillAnimator.AnimationEnd += (_, _) => {
            if (uiDestroyed) return;
            if (!targetExpanded && !isFormatPillExpanded) {
                RebuildFormatPillChips(forceExpandedChips: false);
                formatExpandFraction = 0f;
                ApplyFormatPillExpandFraction(0f);
            }
        };
        formatPillAnimator.Start();
    }

    void ApplyFormatPillExpandFraction(float f) {
        if (musicSpatialBadgePill == null || musicSpatialBadgeText == null || musicSpatialBadgeIcon == null) {
            return;
        }
        f = Math.Clamp(f, 0f, 1f);

        // Measure natural full width of spatial badge text
        string txt = musicSpatialBadgeText.Text ?? "";
        int measuredTextW = Math.Max(Dp(20), (int)Math.Ceiling(musicSpatialBadgeText.Paint.MeasureText(txt)) + Dp(4));
        spatialBadgeFullTextWidthPx = measuredTextW;

        int rowW = badgeFormatRow?.Width > 0
            ? badgeFormatRow.Width
            : Math.Max(Dp(280), (Resources?.DisplayMetrics?.WidthPixels ?? 1080) - Dp(40));

        int floorSpatialW = Dp(26); // Guaranteed 26dp x 26dp circular icon floor
        int fullSpatialW = Dp(18) + Dp(12) + Dp(5) + spatialBadgeFullTextWidthPx;
        int gapPx = musicFormatPillContainer?.Visibility == ViewStates.Visible ? Dp(7) : 0;

        if ((f <= 0.001f && !isFormatPillShowingExpandedChips) || musicFormatPillContainer?.Visibility != ViewStates.Visible) {
            if (musicSpatialBadgePill.LayoutParameters is LinearLayout.LayoutParams spLp0) {
                spLp0.Width = ViewGroup.LayoutParams.WrapContent;
                musicSpatialBadgePill.LayoutParameters = spLp0;
            }
            musicSpatialBadgePill.SetPadding(Dp(9), 0, Dp(9), 0);
            if (musicSpatialBadgeIcon.LayoutParameters is LinearLayout.LayoutParams icoLp0) {
                icoLp0.RightMargin = Dp(5);
                musicSpatialBadgeIcon.LayoutParameters = icoLp0;
            }
            if (musicSpatialBadgeText.LayoutParameters is LinearLayout.LayoutParams txtLp0) {
                txtLp0.Width = ViewGroup.LayoutParams.WrapContent;
                musicSpatialBadgeText.LayoutParameters = txtLp0;
            }
            musicSpatialBadgeText.Alpha = 1f;
            musicSpatialBadgeText.Visibility = ViewStates.Visible;

            if (musicFormatPillContainer?.LayoutParameters is LinearLayout.LayoutParams fmtLp0) {
                fmtLp0.Width = ViewGroup.LayoutParams.WrapContent;
                musicFormatPillContainer.LayoutParameters = fmtLp0;
            }
            return;
        }

        // Smooth clipped width interpolation for the format pill (collapsedW -> targetExpandedFormatW)
        int maxExpandedFormatW = Math.Max(Dp(72), rowW - gapPx - floorSpatialW);
        int targetExpandedFormatW = Math.Min(Math.Max(formatExpandedWidthPx, Dp(72)), maxExpandedFormatW);
        int startCollapsedFormatW = Math.Clamp(formatCollapsedWidthPx > 0 ? formatCollapsedWidthPx : Dp(68), Dp(52), targetExpandedFormatW);
        int curFormatW = (int)Math.Round(startCollapsedFormatW + (targetExpandedFormatW - startCollapsedFormatW) * f);

        // Spatial badge contracts only as much as needed to make room, smoothly down to the 26dp circular icon floor
        int availableSpatialW = Math.Max(floorSpatialW, rowW - gapPx - curFormatW);
        int curSpatialW = Math.Clamp(availableSpatialW, floorSpatialW, fullSpatialW);
        float shrinkProgress = fullSpatialW > floorSpatialW
            ? Math.Clamp((fullSpatialW - curSpatialW) / (float)(fullSpatialW - floorSpatialW), 0f, 1f)
            : 0f;

        int padH = (int)Math.Round(Dp(9) * (1f - shrinkProgress));
        int icoRight = (int)Math.Round(Dp(5) * (1f - shrinkProgress));
        int textAreaW = Math.Max(0, curSpatialW - (padH * 2) - Dp(12) - icoRight);

        if (musicSpatialBadgePill.LayoutParameters is LinearLayout.LayoutParams spLp) {
            spLp.Width = Math.Max(floorSpatialW, curSpatialW);
            musicSpatialBadgePill.LayoutParameters = spLp;
        }
        musicSpatialBadgePill.SetPadding(padH, 0, padH, 0);
        if (musicSpatialBadgeIcon.LayoutParameters is LinearLayout.LayoutParams icoLp) {
            icoLp.RightMargin = icoRight;
            musicSpatialBadgeIcon.LayoutParameters = icoLp;
        }
        if (textAreaW <= Dp(4)) {
            musicSpatialBadgeText.Visibility = ViewStates.Gone;
            musicSpatialBadgeText.Alpha = 0f;
        } else {
            musicSpatialBadgeText.Visibility = ViewStates.Visible;
            musicSpatialBadgeText.Alpha = Math.Clamp(1f - shrinkProgress * 1.35f, 0f, 1f);
            if (musicSpatialBadgeText.LayoutParameters is LinearLayout.LayoutParams txtLp) {
                txtLp.Width = textAreaW;
                musicSpatialBadgeText.LayoutParameters = txtLp;
            }
        }

        if (musicFormatPillContainer?.LayoutParameters is LinearLayout.LayoutParams fmtLp) {
            fmtLp.Width = curFormatW;
            musicFormatPillContainer.LayoutParameters = fmtLp;
        }

        // Cross-fade secondary variants & category logos smoothly while keeping the active variant anchored at full opacity
        if (musicFormatSwitchRow != null && isFormatPillShowingExpandedChips) {
            float extraAlpha = Math.Clamp((f - 0.10f) / 0.75f, 0f, 1f);
            for (int i = 0; i < musicFormatSwitchRow.ChildCount; i++) {
                View child = musicFormatSwitchRow.GetChildAt(i);
                if (child != null && string.Equals(child.Tag?.ToString(), "extra", StringComparison.Ordinal)) {
                    child.Alpha = extraAlpha;
                }
            }
        }
    }

}
