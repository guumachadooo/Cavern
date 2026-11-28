using Android.Animation;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

public partial class MainActivity {
    // W55.4.1: 3-Tab Shell Top Header, Persistent Frosted Mini-Player, & Full-Screen Now Playing Sheet
    LinearLayout topShellHeader;
    TextView topShellTitleText;
    FrameLayout topShellSettingsGearBtn;

    OneUiFrostedCapsuleView miniPlayerBarHost;
    RoundedArtworkView miniPlayerThumbView;
    TextView miniPlayerTitleText, miniPlayerSubText;
    FrameLayout miniPlayerPlayBtn, miniPlayerNextBtn;
    OneUiIconView miniPlayerPlayIcon;
    View miniPlayerProgressFill;
    FrameLayout miniPlayerProgressTrack;

    LinearLayout nowPlayingSheetHost;
    FrameLayout nowPlayingSheetContentHost;
    ScrollView stage3dScrollView;
    TextView[] nowPlayingSubBtns = new TextView[3];
    bool isNowPlayingSheetOpen;
    int activeNowPlayingSubView; // 0 = Cover, 1 = Lyrics, 2 = 3D Stage
    int nowPlayingSheetAnimGen;

    /// <summary>
    /// Compact One UI 9 Top Shell Header showing active tab title + top-right Settings Gear with OTA indicator dot.
    /// </summary>
    LinearLayout BuildTopShellHeader() {
        topShellHeader = new LinearLayout(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        topShellHeader.SetGravity(GravityFlags.CenterVertical);
        topShellHeader.SetPadding(Dp(20), Dp(8), Dp(14), Dp(4));

        topShellTitleText = new TextView(this) {
            Text = GetActiveTabHeaderTitle(),
            TextSize = 21f
        };
        topShellTitleText.SetTextColor(ColText);
        topShellTitleText.Typeface = OneUiTheme.GetAppleFontBold(this);
        topShellTitleText.SetSingleLine(true);
        topShellTitleText.Ellipsize = TextUtils.TruncateAt.End;
        topShellTitleText.SetIncludeFontPadding(false);
        topShellHeader.AddView(topShellTitleText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        topShellSettingsGearBtn = new FrameLayout(this) {
            Clickable = true,
            Focusable = true,
            ContentDescription = S["TabSettings"],
            Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f)
        };
        OneUiIconView gearIcon = new(this, OneUiIconType.Gear, ColText);
        topShellSettingsGearBtn.AddView(gearIcon, new FrameLayout.LayoutParams(Dp(20), Dp(20)) {
            Gravity = GravityFlags.Center
        });

        settingsGearOtaDot = new View(this) {
            Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.IconAtmosUpmix, 99f),
            Visibility = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone
        };
        settingsUpdateDot = settingsGearOtaDot;
        topShellSettingsGearBtn.AddView(settingsGearOtaDot, new FrameLayout.LayoutParams(Dp(7), Dp(7)) {
            Gravity = GravityFlags.Top | GravityFlags.End,
            TopMargin = Dp(7),
            RightMargin = Dp(7)
        });
        OneUiTheme.AttachRecoil(topShellSettingsGearBtn, 0.92f);
        topShellSettingsGearBtn.Click += (_, _) => ShowAppSettingsSheet();

        topShellHeader.AddView(topShellSettingsGearBtn, new LinearLayout.LayoutParams(Dp(40), Dp(40)));
        return topShellHeader;
    }

    string GetActiveTabHeaderTitle() => activeTab switch {
        1 => S["TabSound"],
        2 => S["TabExport"],
        _ => S["TabLibrary"]
    };

    void UpdateTopShellHeaderTitle() {
        if (topShellTitleText != null) {
            topShellTitleText.Text = GetActiveTabHeaderTitle();
        }
    }

    /// <summary>
    /// Persistent One UI 9 frosted glass Mini-Player bar docked above the 3-tab floating capsule.
    /// Tapping opens the Full-Screen Now Playing Sheet (Cover | Lyrics | 3D Stage).
    /// </summary>
    OneUiFrostedCapsuleView BuildPersistentMiniPlayerBar(View behindContentView) {
        miniPlayerBarHost = new OneUiFrostedCapsuleView(this) {
            CornerRadiusDp = 22f,
            GlazeTopColor = OneUiTheme.SurfaceFloatingBar,
            GlazeBottomColor = OneUiTheme.SurfaceFloatingBar,
            BehindViewsProvider = () => [backdropView, behindContentView],
            Elevation = Dp(12)
        };
        miniPlayerBarHost.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));

        LinearLayout outerCol = new(this) { Orientation = Orientation.Vertical };

        LinearLayout row = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        row.SetGravity(GravityFlags.CenterVertical);

        miniPlayerThumbView = new RoundedArtworkView(this) {
            PreferredSizeDp = 40,
            CornerDp = 10f
        };
        row.AddView(miniPlayerThumbView, new LinearLayout.LayoutParams(Dp(40), Dp(40)) {
            RightMargin = Dp(10)
        });

        LinearLayout textCol = new(this) { Orientation = Orientation.Vertical };
        miniPlayerTitleText = new TextView(this) {
            Text = S["PlayerNoTrackTitle"],
            TextSize = 13.5f
        };
        miniPlayerTitleText.SetTextColor(ColText);
        miniPlayerTitleText.Typeface = OneUiTheme.GetAppleFontSemibold(this);
        miniPlayerTitleText.SetSingleLine(true);
        miniPlayerTitleText.Ellipsize = TextUtils.TruncateAt.End;
        miniPlayerTitleText.SetIncludeFontPadding(false);
        textCol.AddView(miniPlayerTitleText);

        miniPlayerSubText = new TextView(this) {
            Text = S["MiniPlayerTapHint"],
            TextSize = 11.2f
        };
        miniPlayerSubText.SetTextColor(OneUiTheme.TextSecondary);
        miniPlayerSubText.Typeface = OneUiTheme.GetAppleFontRegular(this);
        miniPlayerSubText.SetSingleLine(true);
        miniPlayerSubText.Ellipsize = TextUtils.TruncateAt.End;
        miniPlayerSubText.SetIncludeFontPadding(false);
        textCol.AddView(miniPlayerSubText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2)
        });
        row.AddView(textCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
            RightMargin = Dp(6)
        });

        miniPlayerPlayBtn = new FrameLayout(this) {
            Clickable = true,
            Focusable = true,
            Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Argb(36, 255, 255, 255), 99f)
        };
        miniPlayerPlayIcon = new OneUiIconView(this, OneUiIconType.Play, ColText);
        miniPlayerPlayBtn.AddView(miniPlayerPlayIcon, new FrameLayout.LayoutParams(Dp(18), Dp(18)) {
            Gravity = GravityFlags.Center
        });
        OneUiTheme.AttachRecoil(miniPlayerPlayBtn, 0.90f);
        miniPlayerPlayBtn.Click += (_, _) => TogglePlayPause();
        row.AddView(miniPlayerPlayBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)) {
            RightMargin = Dp(4)
        });

        miniPlayerNextBtn = new FrameLayout(this) {
            Clickable = true,
            Focusable = true,
            Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Transparent, 99f)
        };
        OneUiIconView nextIcon = new(this, OneUiIconType.SkipNext, OneUiTheme.TextSecondary);
        miniPlayerNextBtn.AddView(nextIcon, new FrameLayout.LayoutParams(Dp(18), Dp(18)) {
            Gravity = GravityFlags.Center
        });
        OneUiTheme.AttachRecoil(miniPlayerNextBtn, 0.90f);
        miniPlayerNextBtn.Click += (_, _) => HandleMusicSkipNext();
        row.AddView(miniPlayerNextBtn, new LinearLayout.LayoutParams(Dp(34), Dp(38)));

        outerCol.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        miniPlayerProgressTrack = new FrameLayout(this) {
            Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(40, 255, 255, 255), 99f)
        };
        miniPlayerProgressFill = new View(this) {
            Background = OneUiTheme.CreateRoundedDrawable(this, ColAccent, 99f)
        };
        miniPlayerProgressTrack.AddView(miniPlayerProgressFill, new FrameLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent));
        outerCol.AddView(miniPlayerProgressTrack, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2)) {
            TopMargin = Dp(5)
        });

        miniPlayerBarHost.AddView(outerCol, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        OneUiTheme.AttachRecoil(miniPlayerBarHost, 0.98f);
        miniPlayerBarHost.Click += (_, _) => OpenNowPlayingSheet(subViewIndex: 0);
        return miniPlayerBarHost;
    }

    /// <summary>
    /// Full-Screen Now Playing Sheet overlay unifying Cover (0) | Lyrics (1) | 3D Stage (2).
    /// </summary>
    LinearLayout BuildNowPlayingSheetOverlay(LinearLayout stage3dPage) {
        nowPlayingSheetHost = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone,
            Clickable = true,
            Focusable = true
        };
        nowPlayingSheetHost.SetBackgroundColor(Color.Argb(248, ColBg.R, ColBg.G, ColBg.B));

        // Top Sheet Header: [Collapse Chevron]  [Cover | Lyrics | 3D Stage]  [Settings Gear]
        LinearLayout header = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(12), Dp(8), Dp(12), Dp(6));

        FrameLayout collapseBtn = new(this) {
            Clickable = true,
            Focusable = true,
            ContentDescription = S["CloseBtn"],
            Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f)
        };
        OneUiIconView collapseIcon = new(this, OneUiIconType.ChevronDown, ColText);
        collapseBtn.AddView(collapseIcon, new FrameLayout.LayoutParams(Dp(20), Dp(20)) { Gravity = GravityFlags.Center });
        OneUiTheme.AttachRecoil(collapseBtn, 0.90f);
        collapseBtn.Click += (_, _) => CloseNowPlayingSheet();
        header.AddView(collapseBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)) { RightMargin = Dp(8) });

        LinearLayout segWrap = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false,
            Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(44, 255, 255, 255), 999f, Color.Argb(38, 255, 255, 255), 1f)
        };
        segWrap.SetPadding(Dp(3), Dp(3), Dp(3), Dp(3));
        string[] subLabels = [S["NowPlayingCoverTab"], S["NowPlayingLyricsTab"], S["NowPlaying3dStageTab"]];
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(this);
        for (int i = 0; i < 3; i++) {
            int subIdx = i;
            TextView tv = new(this) {
                Text = subLabels[i],
                TextSize = 11.8f,
                Gravity = GravityFlags.Center
            };
            tv.SetTypeface(sfSemi, TypefaceStyle.Bold);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            tv.SetIncludeFontPadding(false);
            tv.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
            OneUiTheme.AttachRecoil(tv, 0.94f);
            tv.Click += (_, _) => SelectNowPlayingSubView(subIdx);
            nowPlayingSubBtns[i] = tv;
            segWrap.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
        header.AddView(segWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        FrameLayout sheetGearBtn = new(this) {
            Clickable = true,
            Focusable = true,
            ContentDescription = S["TabSettings"],
            Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f)
        };
        OneUiIconView sheetGearIcon = new(this, OneUiIconType.Gear, ColText);
        sheetGearBtn.AddView(sheetGearIcon, new FrameLayout.LayoutParams(Dp(18), Dp(18)) { Gravity = GravityFlags.Center });
        OneUiTheme.AttachRecoil(sheetGearBtn, 0.90f);
        sheetGearBtn.Click += (_, _) => ShowAppSettingsSheet();
        header.AddView(sheetGearBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)));

        nowPlayingSheetHost.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Sheet Content Stack: musicNowPlayingScrollView (Cover / Lyrics) + stage3dScrollView (3D Stage)
        nowPlayingSheetContentHost = new FrameLayout(this);
        if (musicNowPlayingScrollView != null) {
            if (musicNowPlayingScrollView.Parent is ViewGroup oldParent) {
                oldParent.RemoveView(musicNowPlayingScrollView);
            }
            musicNowPlayingScrollView.SetPadding(Dp(20), Dp(2), Dp(20), Dp(14));
            musicNowPlayingScrollView.Visibility = ViewStates.Visible;
            nowPlayingSheetContentHost.AddView(musicNowPlayingScrollView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent));
        }

        stage3dScrollView = new ScrollView(this) {
            FillViewport = true,
            VerticalScrollBarEnabled = true,
            Visibility = ViewStates.Gone
        };
        stage3dScrollView.SetClipToPadding(false);
        stage3dScrollView.SetBackgroundColor(Color.Transparent);
        if (stage3dPage != null) {
            stage3dPage.SetPadding(Dp(14), Dp(4), Dp(14), Dp(24));
            stage3dScrollView.AddView(stage3dPage);
        }
        nowPlayingSheetContentHost.AddView(stage3dScrollView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        nowPlayingSheetContentHost.LayoutChange += (_, _) => {
            if (isNowPlayingSheetOpen && activeNowPlayingSubView != 2) {
                UpdatePlayTabSpacingForViewport();
            }
        };

        nowPlayingSheetHost.AddView(nowPlayingSheetContentHost, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        UpdateNowPlayingSubViewPillsUi();
        return nowPlayingSheetHost;
    }

    void OpenNowPlayingSheet(int subViewIndex = 0) {
        if (uiDestroyed || nowPlayingSheetHost == null) {
            return;
        }
        bool wasOpen = isNowPlayingSheetOpen;
        isNowPlayingSheetOpen = true;
        isLibrarySectionOpen = false;
        SelectNowPlayingSubView(Math.Clamp(subViewIndex, 0, 2));

        if (librarySearchInput != null) {
            try {
                librarySearchInput.ClearFocus();
                if (GetSystemService(InputMethodService) is Android.Views.InputMethods.InputMethodManager imm) {
                    imm.HideSoftInputFromWindow(librarySearchInput.WindowToken, Android.Views.InputMethods.HideSoftInputFlags.None);
                }
            } catch {
                // Best-effort keyboard dismissal
            }
        }

        if (!wasOpen) {
            int myGen = ++nowPlayingSheetAnimGen;
            nowPlayingSheetHost.Animate()?.Cancel();
            OneUiMotionBlur.EnableHardwareLayer(nowPlayingSheetHost);
            nowPlayingSheetHost.Visibility = ViewStates.Visible;
            nowPlayingSheetHost.Alpha = 0f;
            nowPlayingSheetHost.TranslationY = Dp(48);
            nowPlayingSheetHost.Animate()
                .Alpha(1f)
                .TranslationY(0f)
                .SetDuration(240)
                .SetInterpolator(CreateOneUiEase())
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != nowPlayingSheetAnimGen) return;
                    OneUiMotionBlur.ClearMotionBlur(nowPlayingSheetHost);
                    OneUiMotionBlur.ClearHardwareLayer(nowPlayingSheetHost);
                    if (activeNowPlayingSubView != 2) {
                        UpdatePlayTabSpacingForViewport(force: true);
                    }
                }))
                .Start();
        }
        SetBottomNavBarVisible(false, animate: !wasOpen);
        SyncPredictiveBackRegistration();
    }

    void CloseNowPlayingSheet() {
        if (uiDestroyed || nowPlayingSheetHost == null) {
            return;
        }
        if (!isNowPlayingSheetOpen) {
            isLibrarySectionOpen = true;
            return;
        }
        isNowPlayingSheetOpen = false;
        isLibrarySectionOpen = true;
        musicVideoStage?.PauseVideoDecoderOnly();

        int myGen = ++nowPlayingSheetAnimGen;
        OneUiMotionBlur.EnableHardwareLayer(nowPlayingSheetHost);
        nowPlayingSheetHost.Animate()?.Cancel();
        nowPlayingSheetHost.Animate()
            .Alpha(0f)
            .TranslationY(Dp(48))
            .SetDuration(210)
            .SetInterpolator(new Android.Views.Animations.AccelerateInterpolator(1.25f))
            .WithEndAction(new Java.Lang.Runnable(() => {
                if (uiDestroyed || myGen != nowPlayingSheetAnimGen) return;
                OneUiMotionBlur.ClearMotionBlur(nowPlayingSheetHost);
                    OneUiMotionBlur.ClearHardwareLayer(nowPlayingSheetHost);
                nowPlayingSheetHost.Visibility = ViewStates.Gone;
                nowPlayingSheetHost.TranslationY = 0f;
                nowPlayingSheetHost.Alpha = 1f;
            }))
            .Start();

        SetBottomNavBarVisible(true, animate: true);
        SyncPredictiveBackRegistration();
    }

    void SelectNowPlayingSubView(int subViewIndex) {
        activeNowPlayingSubView = Math.Clamp(subViewIndex, 0, 2);
        UpdateNowPlayingSubViewPillsUi();

        bool showStage3d = activeNowPlayingSubView == 2;
        if (musicNowPlayingScrollView != null) {
            musicNowPlayingScrollView.Visibility = showStage3d ? ViewStates.Gone : ViewStates.Visible;
        }
        if (stage3dScrollView != null) {
            stage3dScrollView.Visibility = showStage3d ? ViewStates.Visible : ViewStates.Gone;
        }

        if (showStage3d) {
            musicVideoStage?.PauseVideoDecoderOnly();
            Refresh3DStageProVisibility();
            UpdateRoomCanvas();
            RefreshSpatialChoreoClarifierUi();
        } else if (activeNowPlayingSubView == 1) {
            SetPlayerStageMode(PlayerStageMode.Lyrics, animate: false);
            RefreshLyricsSingDockUi();
        } else {
            bool hasVideo = currentMediaMeta?.HasVideo == true;
            SetPlayerStageMode(hasVideo ? PlayerStageMode.Video : PlayerStageMode.Cover, animate: false);
            nowPlayingSheetContentHost?.Post(() => UpdatePlayTabSpacingForViewport(force: true));
        }
        SyncPredictiveBackRegistration();
    }

    void UpdateNowPlayingSubViewPillsUi() {
        for (int i = 0; i < nowPlayingSubBtns.Length; i++) {
            TextView btn = nowPlayingSubBtns[i];
            if (btn == null) continue;
            bool sel = i == activeNowPlayingSubView;
            btn.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(228, 255, 255, 255), 999f)
                : null;
            btn.SetTextColor(sel ? Color.Argb(255, 18, 19, 24) : Color.Argb(215, 240, 242, 248));
        }
    }

    void RefreshMiniPlayerUi() {
        if (uiDestroyed || miniPlayerBarHost == null) {
            return;
        }
        string title = string.IsNullOrWhiteSpace(musicTitleText?.Text) ? S["PlayerNoTrackTitle"] : musicTitleText.Text;
        string sub = string.IsNullOrWhiteSpace(musicArtistText?.Text) ? S["MiniPlayerTapHint"] : musicArtistText.Text;
        if (miniPlayerTitleText != null) {
            miniPlayerTitleText.Text = title;
        }
        if (miniPlayerSubText != null) {
            miniPlayerSubText.Text = sub;
        }
        if (miniPlayerThumbView != null) {
            Color accent = currentMediaMeta?.PrimaryAccent ?? OneUiTheme.Primary;
            Bitmap activeArt = (LoadedFile != null && IsBitmapUsable(currentMediaMeta?.ArtworkBitmap))
                ? currentMediaMeta.ArtworkBitmap
                : defaultFallbackCover;
            miniPlayerThumbView.SetArtwork(activeArt, LoadedFile != null ? accent : OneUiTheme.Primary);
        }
        if (miniPlayerPlayIcon != null) {
            miniPlayerPlayIcon.IconType = playerEngine?.IsPlaying == true ? OneUiIconType.Pause : OneUiIconType.Play;
        }
    }

    void RefreshMiniPlayerPlaybackUi(bool isPlaying, double curSeconds, double totSeconds) {
        if (uiDestroyed || miniPlayerBarHost == null) {
            return;
        }
        if (miniPlayerPlayIcon != null) {
            OneUiIconType targetIcon = isPlaying ? OneUiIconType.Pause : OneUiIconType.Play;
            if (miniPlayerPlayIcon.IconType != targetIcon) {
                miniPlayerPlayIcon.IconType = targetIcon;
            }
        }
        if (miniPlayerProgressFill != null && miniPlayerProgressTrack != null) {
            int trackW = miniPlayerProgressTrack.Width;
            if (trackW > 0) {
                double frac = totSeconds > 0.01 ? Math.Clamp(curSeconds / totSeconds, 0.0, 1.0) : 0.0;
                int fillW = (int)Math.Round(trackW * frac);
                if (miniPlayerProgressFill.LayoutParameters is FrameLayout.LayoutParams flp && flp.Width != fillW) {
                    flp.Width = fillW;
                    miniPlayerProgressFill.LayoutParameters = flp;
                }
            }
        }
    }
}
