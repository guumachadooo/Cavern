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
    // ============================================================
    // IN-APP LIBRARY UI (Unified IA, Smart Clean v2 Inspector, Variant Merge & Cached Index)
    // ============================================================
    LinearLayout BuildInAppLibrarySection(Typeface sfBold, Typeface sfSemi, Typeface sfReg) {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };

        // 1. Search Pill (180ms debounce + Clear 'X') + Refresh Button
        LinearLayout searchRow = HRow();
        searchRow.SetPadding(0, Dp(2), 0, Dp(8));

        LinearLayout searchPill = HRow();
        searchPill.SetPadding(Dp(12), Dp(5), Dp(8), Dp(5));
        searchPill.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 999f);
        OneUiIconView srchIco = new(this, OneUiIconType.Search, OneUiTheme.TextMuted);

        librarySearchInput = new EditText(this) {
            Hint = S["LibSearchHint"],
            TextSize = 12.5f,
            ImeOptions = Android.Views.InputMethods.ImeAction.Search
        };
        librarySearchInput.SetTypeface(sfReg, TypefaceStyle.Normal);
        librarySearchInput.SetTextColor(ColText);
        librarySearchInput.SetHintTextColor(ColMuted);
        librarySearchInput.SetBackgroundColor(Color.Transparent);
        librarySearchInput.SetSingleLine(true);
        librarySearchInput.SetPadding(0, Dp(2), 0, Dp(2));

        librarySearchClearBtn = new FrameLayout(this) {
            Visibility = ViewStates.Gone,
            ContentDescription = S["LibClearSearchDesc"]
        };
        librarySearchClearBtn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(36, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(librarySearchClearBtn, 0.88f);
        librarySearchClearBtn.AddView(new SearchClearIconView(this, OneUiTheme.TextMuted),
            new FrameLayout.LayoutParams(Dp(12), Dp(12)) { Gravity = GravityFlags.Center });
        librarySearchClearBtn.Click += (_, _) => {
            CancelLibrarySearchDebounce();
            librarySearchQuery = "";
            libraryRenderLimit = 72;
            if (librarySearchInput != null && !string.IsNullOrEmpty(librarySearchInput.Text)) {
                librarySearchInput.Text = "";
            } else {
                RenderLibraryView();
            }
        };

        librarySearchInput.TextChanged += (_, _) => {
            string raw = librarySearchInput.Text ?? "";
            if (librarySearchClearBtn != null) {
                librarySearchClearBtn.Visibility = string.IsNullOrEmpty(raw) ? ViewStates.Gone : ViewStates.Visible;
            }
            CancelLibrarySearchDebounce();
            if (string.IsNullOrEmpty(raw)) {
                bool changed = !string.IsNullOrEmpty(librarySearchQuery);
                librarySearchQuery = "";
                libraryRenderLimit = 72;
                if (changed || isLibrarySectionOpen) {
                    RenderLibraryView();
                }
                return;
            }
            if (string.Equals(raw, librarySearchQuery, StringComparison.Ordinal)) {
                return;
            }
            uiHandler ??= new Android.OS.Handler(Android.OS.Looper.MainLooper);
            librarySearchDebounceRunnable = new Java.Lang.Runnable(() => {
                if (uiDestroyed) return;
                librarySearchQuery = librarySearchInput?.Text ?? "";
                libraryRenderLimit = 72;
                RenderLibraryView();
            });
            uiHandler.PostDelayed(librarySearchDebounceRunnable, 180);
        };

        searchPill.AddView(srchIco, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { RightMargin = Dp(8) });
        searchPill.AddView(librarySearchInput, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        searchPill.AddView(librarySearchClearBtn, new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        searchRow.AddView(searchPill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        FrameLayout refreshBtn = MakeAppleCircleIconBtn(OneUiIconType.Refresh, () => RefreshLibraryScanAsync(force: true));
        searchRow.AddView(refreshBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)));
        box.AddView(searchRow);

        // 2. Non-Scrolling Segmented Category Capsule: [ Albums | Tracks | Artists | Folders ]
        LinearLayout catCapsule = HRow();
        catCapsule.SetPadding(Dp(3), Dp(3), Dp(3), Dp(3));
        catCapsule.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(46, 255, 255, 255), 999f, Color.Argb(38, 255, 255, 255), 1f);
        (LibraryCategory cat, string title)[] cats = [
            (LibraryCategory.Albums, S["LibAlbums"]),
            (LibraryCategory.Tracks, S["LibTracks"]),
            (LibraryCategory.Artists, S["LibArtists"]),
            (LibraryCategory.Folders, S["LibFolders"])
        ];
        libCatTabs = new TextView[cats.Length];
        for (int i = 0; i < cats.Length; i++) {
            int idx = i;
            LibraryCategory c = cats[i].cat;
            TextView tabTv = new(this) {
                Text = cats[i].title,
                TextSize = 11.5f,
                Gravity = GravityFlags.Center
            };
            tabTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
            tabTv.SetIncludeFontPadding(false);
            tabTv.SetSingleLine(true);
            tabTv.Ellipsize = TextUtils.TruncateAt.End;
            tabTv.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
            OneUiTheme.AttachRecoil(tabTv, 0.94f);
            tabTv.Click += (_, _) => {
                if (libraryCategory == c &&
                    selectedLibraryAlbum == null &&
                    string.IsNullOrEmpty(selectedLibraryArtist) &&
                    selectedLibraryFolder == null &&
                    libraryListHost?.ChildCount > 0) {
                    return;
                }
                int prevCatIdx = (int)libraryCategory;
                int nextCatIdx = (int)c;
                libraryCategory = c;
                selectedLibraryAlbum = null;
                selectedLibraryArtist = null;
                selectedLibraryFolder = null;
                libraryRenderLimit = 72;
                UpdateLibraryCatStyles();
                RenderLibraryView();
                if (libraryListHost != null && prevCatIdx != nextCatIdx) {
                    int span = Math.Abs(nextCatIdx - prevCatIdx);
                    float dir = nextCatIdx > prevCatIdx ? 1f : -1f;
                    float distPx = Dp(Math.Min(120, 44 * span));
                    float density = Resources?.DisplayMetrics?.Density ?? 3f;
                    libraryListHost.Animate()?.Cancel();
                    OneUiMotionBlur.ClearMotionBlur(libraryListHost);
                    float prevX = dir * distPx;
                    long prevMs = Android.OS.SystemClock.UptimeMillis();
                    libraryListHost.TranslationX = prevX;
                    ValueAnimator catAnim = ValueAnimator.OfFloat(prevX, 0f);
                    catAnim.SetDuration(220);
                    catAnim.SetInterpolator(new Android.Views.Animations.PathInterpolator(0.22f, 1f, 0.36f, 1f));
                    catAnim.Update += (_, ev) => {
                        if (uiDestroyed || libraryListHost == null) return;
                        float curX = (float)(ev.Animation?.AnimatedValue ?? 0f);
                        long nowMs = Android.OS.SystemClock.UptimeMillis();
                        float vel = MathF.Abs(curX - prevX) / Math.Max(0.004f, (nowMs - prevMs) * 0.001f);
                        prevX = curX;
                        prevMs = nowMs;
                        libraryListHost.TranslationX = curX;
                        OneUiMotionBlur.ApplyHorizontalMotionBlur(
                            libraryListHost,
                            OneUiMotionBlur.ComputeHorizontalBlurRadiusPx(vel * 1.4f, density, maxBlurDp: 14f));
                    };
                    catAnim.AnimationEnd += (_, _) => {
                        if (libraryListHost != null) {
                            libraryListHost.TranslationX = 0f;
                            OneUiMotionBlur.ClearMotionBlur(libraryListHost);
                        }
                    };
                    catAnim.AnimationCancel += (_, _) => OneUiMotionBlur.ClearMotionBlur(libraryListHost);
                    catAnim.Start();
                }
            };
            libCatTabs[idx] = tabTv;
            catCapsule.AddView(tabTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
        box.AddView(catCapsule, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 3. Single Unified Chip & Action Bar: [All|Music|Videos] | [Audio Filter] | [Sort] | [Smart Clean (N)]
        HorizontalScrollView filtScroll = new(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never
        };
        LinearLayout filtRow = HRow();
        filtRow.SetPadding(0, 0, 0, Dp(8));

        LinearLayout mediaTypeGroup = HRow();
        mediaTypeGroup.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
        mediaTypeGroup.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 999f, Color.Argb(34, 255, 255, 255), 0.8f);
        (int mediaMode, string mediaLabel)[] mediaChoices = [
            (0, S["LibMediaAll"]),
            (1, S["LibMediaMusic"]),
            (2, S["LibMediaVideos"])
        ];
        libMediaTypeTabs = new TextView[mediaChoices.Length];
        for (int i = 0; i < mediaChoices.Length; i++) {
            int mMode = mediaChoices[i].mediaMode;
            TextView mTv = new(this) {
                Text = mediaChoices[i].mediaLabel,
                TextSize = 10.8f,
                Gravity = GravityFlags.Center
            };
            mTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
            mTv.SetIncludeFontPadding(false);
            mTv.SetSingleLine(true);
            mTv.Ellipsize = TextUtils.TruncateAt.End;
            mTv.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            mTv.SetMinimumHeight(Dp(26));
            OneUiTheme.AttachRecoil(mTv, 0.94f);
            mTv.Click += (_, _) => {
                if (store.LibraryMediaTypeFilter == mMode) return;
                store.LibraryMediaTypeFilter = mMode;
                libraryRenderLimit = 72;
                InvalidateLibraryIndexCache();
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            };
            libMediaTypeTabs[i] = mTv;
            mediaTypeGroup.AddView(mTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(26)));
        }
        filtRow.AddView(mediaTypeGroup, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libQualityFilterBtn, libQualityFilterIcon, libQualityFilterText) = MakeCompactSpatialPill(
            OneUiIconType.Sparkles, S["LibQualityAll"], sfSemi, ShowLibraryQualityFilterDialog);
        libQualityFilterBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        filtRow.AddView(libQualityFilterBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libSortBtn, libSortIcon, libSortText) = MakeCompactSpatialPill(
            OneUiIconType.Sliders, S["LibSortPrefix"], sfSemi, ShowLibrarySortDialog);
        libSortBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        filtRow.AddView(libSortBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libNoiseToggleBtn, libNoiseToggleIcon, libNoiseToggleText) = MakeCompactSpatialPill(
            OneUiIconType.CleanFilter, S["LibHideNoise"], sfSemi, ShowSmartCleanInspectorModal);
        libNoiseToggleBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        filtRow.AddView(libNoiseToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libFolderScopeChip, libFolderScopeIcon, libFolderScopeText) = MakeCompactSpatialPill(
            OneUiIconType.FolderFilter, S["LibFoldersChipLabel"], sfSemi, ShowLibraryFolderFilterDialog);
        libFolderScopeChip.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        libFolderScopeChip.Visibility = ViewStates.Gone;
        filtRow.AddView(libFolderScopeChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)));

        filtScroll.AddView(filtRow);
        box.AddView(filtScroll);

        libraryStatusText = Label("", 11.5f, OneUiTheme.TextMuted);
        libraryStatusText.SetPadding(Dp(4), 0, Dp(4), Dp(8));
        box.AddView(libraryStatusText);

        libraryListHost = new LinearLayout(this) { Orientation = Orientation.Vertical };
        ClearLibraryTabViewCache();
        box.AddView(libraryListHost, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        UpdateLibraryCatStyles();
        UpdateLibraryFilterStyles();
        return box;
    }

    int cachedVisibleSpatialCount;
    string cachedLibraryBadgeText = "";
    AudioTierCategory? cachedBuiltPrefCategory;
    string cachedBuiltPrefFormatKey = "";
    List<LibraryTrackItem> cachedMergedTrackQueue = [];
    Dictionary<LibraryArtistGroup, (List<LibraryTrackItem> Queue, int DistinctAlbumCount)> cachedArtistMeta = [];
    Dictionary<LibraryFolderGroup, List<LibraryTrackItem>> cachedFolderQueues = [];
    Java.Lang.Runnable activeLibraryPumpRunnable;

    void CancelLibraryRenderPump() {
        if (activeLibraryPumpRunnable != null) {
            libraryListHost?.RemoveCallbacks(activeLibraryPumpRunnable);
            activeLibraryPumpRunnable = null;
        }
    }

    void CancelLibrarySearchDebounce() {
        if (librarySearchDebounceRunnable != null) {
            uiHandler?.RemoveCallbacks(librarySearchDebounceRunnable);
            librarySearchDebounceRunnable = null;
        }
    }

    void InvalidateLibraryIndexCache() {
        libraryFilterRevision++;
        cachedLibIndexSnap = null;
        cachedQuerySnap = null;
        cachedQueryRevision = -1;
        cachedVisibleSpatialCount = 0;
        cachedLibraryBadgeText = "";
        cachedMergedTrackQueue = [];
        cachedArtistMeta = [];
        cachedFolderQueues = [];
        CancelLibraryRenderPump();
        ClearLibraryTabViewCache();
    }

    string GetLibrarySortShortLabel(int sortMode) => sortMode switch {
        1 => S["LibSortArtistAsc"],
        2 => S["LibSortRecentDesc"],
        3 => S["LibSortQualityDesc"],
        _ => S["LibSortTitleAsc"]
    };

    void UpdateLibraryFilterStyles() {
        if (libMediaTypeTabs != null) {
            int mediaMode = store.LibraryMediaTypeFilter;
            for (int i = 0; i < libMediaTypeTabs.Length; i++) {
                bool sel = mediaMode == i;
                libMediaTypeTabs[i].Background = sel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 170), OneUiTheme.PillCornerDp, OneUiTheme.PrimaryHigh, 0.8f)
                    : null;
                libMediaTypeTabs[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSub);
            }
        }
        if (libQualityFilterBtn != null) {
            int qMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
            libQualityFilterText.Text = qMode switch {
                1 => S["LibFilterSurround"],
                2 => S["LibFilterSpatialOnly"],
                _ => S["LibQualityAll"]
            };
            bool active = qMode > 0;
            libQualityFilterBtn.Background = active
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 145), OneUiTheme.PillCornerDp, OneUiTheme.PrimaryHigh, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            Color fg = active ? Color.White : OneUiTheme.TextSub;
            libQualityFilterIcon.IconColor = fg;
            libQualityFilterText.SetTextColor(fg);
        }
        if (libSortBtn != null) {
            int sMode = store.LibrarySortMode;
            libSortText.Text = $"{S["LibSortPrefix"]}: {GetLibrarySortShortLabel(sMode)}";
            bool active = sMode > 0;
            libSortBtn.Background = active
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Tertiary, 115), OneUiTheme.PillCornerDp, OneUiTheme.Tertiary, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            Color fg = active ? Color.White : OneUiTheme.TextSub;
            libSortIcon.IconColor = fg;
            libSortText.SetTextColor(fg);
        }
        if (libNoiseToggleBtn != null) {
            bool on = store.LibraryHideNonMusic;
            int hiddenCount = librarySnapshot != null ? cachedHiddenNoiseCount : 0;
            libNoiseToggleText.Text = hiddenCount > 0
                ? string.Format(S["SmartCleanChipBadge"], hiddenCount)
                : S["LibHideNoise"];
            libNoiseToggleBtn.Background = on
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Secondary, 115), OneUiTheme.PillCornerDp, OneUiTheme.Secondary, 1f)
                : (hiddenCount > 0
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalAmber, 70), OneUiTheme.PillCornerDp, OneUiTheme.FunctionalAmber, 1f)
                    : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp));
            Color fg = on || hiddenCount > 0 ? Color.White : OneUiTheme.TextSub;
            libNoiseToggleIcon.IconColor = fg;
            libNoiseToggleText.SetTextColor(fg);
        }
        if (libFolderScopeChip != null) {
            int fMode = store.LibraryFolderFilterMode;
            List<string> dirs = store.GetLibraryFilterFolders();
            bool active = fMode > 0 && dirs.Count > 0;
            libFolderScopeChip.Visibility = active ? ViewStates.Visible : ViewStates.Gone;
            if (active) {
                libFolderScopeText.Text = string.Format(S["LibFoldersChipActive"], dirs.Count);
                libFolderScopeChip.Background = OneUiTheme.CreateRoundedDrawable(
                    this, OneUiTheme.WithAlpha(OneUiTheme.Tertiary, 120), OneUiTheme.PillCornerDp, OneUiTheme.Tertiary, 1f);
                libFolderScopeIcon.IconColor = Color.White;
                libFolderScopeText.SetTextColor(Color.White);
            }
        }
    }

    List<LibraryTrackItem> FilterLibraryTracks(IEnumerable<LibraryTrackItem> tracks) =>
        MediaLibraryService.FilterTracks(tracks, store);

    void ShowLibraryQualityFilterDialog() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        int curMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
        (int mode, string label)[] opts = [
            (0, S["LibFilterAll"]),
            (1, S["LibFilterSurround"]),
            (2, S["LibFilterSpatialOnly"])
        ];
        for (int i = 0; i < opts.Length; i++) {
            int m = opts[i].mode;
            bool sel = curMode == m;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 95), 14f, OneUiTheme.PrimaryHigh, 1.2f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
            OneUiTheme.AttachRecoil(row, 0.98f);
            TextView tv = Label(opts[i].label, 13f, sel ? Color.White : OneUiTheme.TextSecondary);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (sel) {
                row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh),
                    new LinearLayout.LayoutParams(Dp(16), Dp(16)) { LeftMargin = Dp(8) });
            }
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (store.LibraryFilterMode != m) {
                    store.LibraryFilterMode = m;
                    libraryRenderLimit = 72;
                    InvalidateLibraryIndexCache();
                    UpdateLibraryFilterStyles();
                    RenderLibraryView();
                }
            };
            content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = i < opts.Length - 1 ? Dp(6) : 0
            });
        }
        dlg = OneUiModalDialog.Show(
            this,
            S["LibQualityDialogTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["CloseBtn"]);
    }

    void ShowLibrarySortDialog() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        int curSort = store.LibrarySortMode;
        (int mode, string label)[] opts = [
            (0, S["LibSortTitleAsc"]),
            (1, S["LibSortArtistAsc"]),
            (2, S["LibSortRecentDesc"]),
            (3, S["LibSortQualityDesc"])
        ];
        for (int i = 0; i < opts.Length; i++) {
            int sMode = opts[i].mode;
            bool sel = curSort == sMode;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 95), 14f, OneUiTheme.PrimaryHigh, 1.2f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
            OneUiTheme.AttachRecoil(row, 0.98f);
            TextView tv = Label(opts[i].label, 13f, sel ? Color.White : OneUiTheme.TextSecondary);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (sel) {
                row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh),
                    new LinearLayout.LayoutParams(Dp(16), Dp(16)) { LeftMargin = Dp(8) });
            }
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (store.LibrarySortMode != sMode) {
                    store.LibrarySortMode = sMode;
                    libraryRenderLimit = 72;
                    InvalidateLibraryIndexCache();
                    UpdateLibraryFilterStyles();
                    RenderLibraryView();
                }
            };
            content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = i < opts.Length - 1 ? Dp(6) : 0
            });
        }
        dlg = OneUiModalDialog.Show(
            this,
            S["LibSortDialogTitle"],
            customContent: content,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: S["CloseBtn"]);
    }

    void ShowSmartCleanInspectorModal() {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(this);
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        Java.Lang.Runnable sliderDebounce = null;
        bool suppressSwitchEvents = false;
        int inspectLimit = 40;

        string FormatMinDur(int sec) => sec <= 0 ? S["SmartCleanMinDurationOff"] : $"{sec}s";

        void CancelInspectorDebounce() {
            if (sliderDebounce != null) {
                uiHandler?.RemoveCallbacks(sliderDebounce);
                sliderDebounce = null;
            }
        }

        LinearLayout dynamicSectionsHost = new(this) { Orientation = Orientation.Vertical };

        void NotifySmartCleanChanged(bool rebuildInspectorList) {
            InvalidateLibraryIndexCache();
            libraryRenderLimit = 72;
            UpdateLibraryFilterStyles();
            RenderLibraryView();
            if (rebuildInspectorList) {
                RebuildDynamicSections();
            }
        }

        Switch masterSw = null, sysSfxSw = null, msgSw = null, camSw = null, stemSw = null;
        OneUiSeekBar minDurBar = null;

        masterSw = AddSwitchRow(box, S["SmartCleanMasterToggle"], store.LibraryHideNonMusic, on => {
            if (suppressSwitchEvents) return;
            store.LibraryHideNonMusic = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });

        TextView rulesHdr = Label(S["SmartCleanRulesHeader"], 12.2f, OneUiTheme.PeachText);
        rulesHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
        rulesHdr.SetPadding(Dp(2), Dp(6), Dp(2), Dp(4));
        box.AddView(rulesHdr);

        sysSfxSw = AddSwitchRow(box, S["SmartCleanCatSystemSfx"], store.SmartCleanHideSystemSfx, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideSystemSfx = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        msgSw = AddSwitchRow(box, S["SmartCleanCatMessengers"], store.SmartCleanHideMessengers, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideMessengers = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        camSw = AddSwitchRow(box, S["SmartCleanCatCameraSocial"], store.SmartCleanHideCameraSocialVideo, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideCameraSocialVideo = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        stemSw = AddSwitchRow(box, S["SmartCleanCatStemsShort"], store.SmartCleanHideStemsAndShort, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideStemsAndShort = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });

        minDurBar = new OneUiSeekBar(this) {
            Max = 180,
            Progress = Math.Clamp(store.SmartCleanMinDurationSec, 0, 180),
            DefaultProgress = 25,
            HasDefaultReset = true,
            LabelText = S["SmartCleanMinDurationLabel"],
            ValueText = FormatMinDur(store.SmartCleanMinDurationSec)
        };
        minDurBar.UserProgressChanged += val => {
            int clamped = Math.Clamp(val, 0, 180);
            minDurBar.ValueText = FormatMinDur(clamped);
            minDurBar.Invalidate();
            store.SmartCleanMinDurationSec = clamped;
            CancelInspectorDebounce();
            uiHandler ??= new Android.OS.Handler(Android.OS.Looper.MainLooper);
            sliderDebounce = new Java.Lang.Runnable(() => {
                if (!uiDestroyed) {
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                }
            });
            uiHandler.PostDelayed(sliderDebounce, 150);
        };
        minDurBar.ResetRequested += val => {
            int clamped = Math.Clamp(val, 0, 180);
            CancelInspectorDebounce();
            minDurBar.ValueText = FormatMinDur(clamped);
            minDurBar.Invalidate();
            store.SmartCleanMinDurationSec = clamped;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        };
        box.AddView(minDurBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        List<string> scopeDirs = store.GetLibraryFilterFolders();
        string scopeSummary = store.LibraryFolderFilterMode > 0 && scopeDirs.Count > 0
            ? string.Format(S["LibFolderScopeSummaryFiltered"], scopeDirs.Count)
            : S["LibFolderScopeSummaryAll"];

        LinearLayout toolsRow = HRow();
        toolsRow.SetPadding(0, 0, 0, Dp(8));
        Button folderScopeBtn = PillButton(string.Format(S["SmartCleanFolderScopeBtn"], scopeSummary), () => {
            dlg?.Dismiss();
            ShowLibraryFolderFilterDialog();
        });
        folderScopeBtn.TextSize = 11.5f;
        Button resetRulesBtn = PillButton(S["SmartCleanResetDefaultsBtn"], () => {
            CancelInspectorDebounce();
            store.ResetSmartCleanToDefaults(clearAllowlistAndHiddenFolders: false);
            suppressSwitchEvents = true;
            masterSw.Checked = store.LibraryHideNonMusic;
            sysSfxSw.Checked = store.SmartCleanHideSystemSfx;
            msgSw.Checked = store.SmartCleanHideMessengers;
            camSw.Checked = store.SmartCleanHideCameraSocialVideo;
            stemSw.Checked = store.SmartCleanHideStemsAndShort;
            suppressSwitchEvents = false;
            minDurBar.Progress = store.SmartCleanMinDurationSec;
            minDurBar.ValueText = FormatMinDur(store.SmartCleanMinDurationSec);
            minDurBar.Invalidate();
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        resetRulesBtn.TextSize = 11.5f;
        toolsRow.AddView(folderScopeBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        toolsRow.AddView(resetRulesBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        box.AddView(toolsRow);

        box.AddView(dynamicSectionsHost);

        void RebuildDynamicSections() {
            if (uiDestroyed) return;
            dynamicSectionsHost.RemoveAllViews();

            HashSet<string> allowlist = store.SmartCleanAllowlistPaths;
            if (allowlist.Count > 0) {
                LinearLayout allowRow = HRow();
                allowRow.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                allowRow.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                TextView allowTv = Label(string.Format(S["SmartCleanAllowlistInfo"], allowlist.Count), 11.8f, OneUiTheme.TextSecondary);
                allowTv.SetSingleLine(true);
                allowTv.Ellipsize = TextUtils.TruncateAt.End;
                Button clearAllowBtn = PillButton(string.Format(S["SmartCleanClearAllowlistBtn"], allowlist.Count), () => {
                    store.ClearSmartCleanAllowlist();
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                });
                clearAllowBtn.TextSize = 11f;
                clearAllowBtn.SetMinimumHeight(Dp(30));
                clearAllowBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
                allowRow.AddView(allowTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                allowRow.AddView(clearAllowBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                dynamicSectionsHost.AddView(allowRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            }

            List<string> hiddenDirs = store.SmartCleanHiddenFolders
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LinearLayout hiddenHdrRow = HRow();
            hiddenHdrRow.SetPadding(Dp(2), Dp(2), Dp(2), Dp(4));
            TextView hiddenHdr = Label(string.Format(S["SmartCleanHiddenFoldersHeader"], hiddenDirs.Count), 12.2f, OneUiTheme.PeachText);
            hiddenHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            hiddenHdrRow.AddView(hiddenHdr, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (hiddenDirs.Count > 1) {
                Button clearDirsBtn = PillButton(S["SmartCleanClearHiddenFoldersBtn"], () => {
                    store.ClearHiddenLibraryFolders();
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                });
                clearDirsBtn.TextSize = 10.8f;
                clearDirsBtn.SetMinimumHeight(Dp(28));
                clearDirsBtn.SetPadding(Dp(9), Dp(3), Dp(9), Dp(3));
                hiddenHdrRow.AddView(clearDirsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            }
            dynamicSectionsHost.AddView(hiddenHdrRow);

            if (hiddenDirs.Count == 0) {
                TextView noDirsTv = Label(S["SmartCleanHiddenFoldersEmpty"], 11.5f, OneUiTheme.TextMuted);
                noDirsTv.SetPadding(Dp(2), 0, Dp(2), Dp(8));
                dynamicSectionsHost.AddView(noDirsTv);
            } else {
                for (int i = 0; i < hiddenDirs.Count; i++) {
                    string dirPath = hiddenDirs[i];
                    string dirLeaf = Path.GetFileName(dirPath.TrimEnd('/'));
                    if (string.IsNullOrEmpty(dirLeaf)) dirLeaf = dirPath;
                    LinearLayout dirRow = HRow();
                    dirRow.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                    dirRow.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                    LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                    TextView nameTv = Label(dirLeaf, 12.2f, ColText);
                    nameTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
                    nameTv.SetSingleLine(true);
                    nameTv.Ellipsize = TextUtils.TruncateAt.End;
                    TextView pathTv = Label(dirPath, 10.5f, OneUiTheme.TextMuted);
                    pathTv.SetSingleLine(true);
                    pathTv.Ellipsize = TextUtils.TruncateAt.Middle;
                    col.AddView(nameTv);
                    col.AddView(pathTv);
                    Button unhideBtn = PillButton(S["SmartCleanUnhideFolderBtn"], () => {
                        store.UnhideLibraryFolder(dirPath);
                        NotifySmartCleanChanged(rebuildInspectorList: true);
                    });
                    unhideBtn.TextSize = 11f;
                    unhideBtn.SetMinimumHeight(Dp(30));
                    unhideBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
                    dirRow.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                    dirRow.AddView(unhideBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                    dynamicSectionsHost.AddView(dirRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        BottomMargin = Dp(6)
                    });
                }
            }

            List<SmartCleanInspectionEntry> entries = (cachedSmartCleanEntries != null &&
                cachedLibIndexSnap == librarySnapshot &&
                cachedLibIndexRevision == libraryFilterRevision)
                ? cachedSmartCleanEntries
                : MediaLibraryService.GetSmartCleanInspectionList(librarySnapshot?.Tracks, store);
            TextView inspHdr = Label(string.Format(S["SmartCleanInspectedHeader"], entries.Count), 12.2f, OneUiTheme.PeachText);
            inspHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            inspHdr.SetPadding(Dp(2), Dp(6), Dp(2), Dp(4));
            dynamicSectionsHost.AddView(inspHdr);

            if (entries.Count == 0) {
                TextView emptyTv = Label(S["SmartCleanInspectedEmpty"], 11.5f, OneUiTheme.TextMuted);
                emptyTv.SetPadding(Dp(2), 0, Dp(2), Dp(4));
                dynamicSectionsHost.AddView(emptyTv);
                return;
            }

            int showCount = Math.Min(entries.Count, inspectLimit);
            for (int i = 0; i < showCount; i++) {
                SmartCleanInspectionEntry entry = entries[i];
                LibraryTrackItem t = entry.Track;
                string fileName = !string.IsNullOrEmpty(t.FilePath) && !t.FilePath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(t.FilePath)
                    : t.Title;
                if (string.IsNullOrWhiteSpace(fileName)) fileName = t.Title;
                string durStr = "";
                if (t.DurationMs > 0) {
                    TimeSpan ts = TimeSpan.FromMilliseconds(t.DurationMs);
                    durStr = ts.TotalHours >= 1 ? $" • {ts:h\\:mm\\:ss}" : $" • {ts:mm\\:ss}";
                }
                string folderLine = $"{t.FolderName}{durStr}";
                string reasonText = !string.IsNullOrEmpty(entry.ReasonKey) ? S[entry.ReasonKey] : entry.ReasonLabel;
                if (string.Equals(reasonText, entry.ReasonKey, StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.ReasonLabel)) {
                    reasonText = entry.ReasonLabel;
                }

                LinearLayout itemRow = HRow();
                itemRow.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                itemRow.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);

                LinearLayout infoCol = new(this) { Orientation = Orientation.Vertical };
                TextView fileTv = Label(fileName, 12.2f, ColText);
                fileTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
                fileTv.SetSingleLine(true);
                fileTv.Ellipsize = TextUtils.TruncateAt.End;
                TextView fldTv = Label(folderLine, 10.5f, OneUiTheme.TextMuted);
                fldTv.SetSingleLine(true);
                fldTv.Ellipsize = TextUtils.TruncateAt.End;
                TextView reasonTv = Label(reasonText, 10.8f, OneUiTheme.PeachText);
                reasonTv.SetSingleLine(true);
                reasonTv.Ellipsize = TextUtils.TruncateAt.End;
                infoCol.AddView(fileTv);
                infoCol.AddView(fldTv);
                infoCol.AddView(reasonTv);

                Button restoreBtn = PillButton(S["SmartCleanRestoreBtn"], () => {
                    string restoreKey = !string.IsNullOrEmpty(t.FilePath) ? t.FilePath : t.CacheKey;
                    store.AllowSmartCleanPath(restoreKey);
                    Toast.MakeText(this, string.Format(S["SmartCleanRestoredToast"], fileName), ToastLength.Short).Show();
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                });
                restoreBtn.TextSize = 11f;
                restoreBtn.SetMinimumHeight(Dp(30));
                restoreBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));

                itemRow.AddView(infoCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                itemRow.AddView(restoreBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                dynamicSectionsHost.AddView(itemRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }

            if (entries.Count > showCount) {
                Button showAllBtn = PillButton(string.Format(S["SmartCleanInspectedMore"], entries.Count), () => {
                    inspectLimit = entries.Count;
                    RebuildDynamicSections();
                });
                showAllBtn.TextSize = 11.5f;
                dynamicSectionsHost.AddView(showAllBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(2)
                });
            }
        }

        RebuildDynamicSections();

        dlg = OneUiModalDialog.Show(
            this,
            S["SmartCleanModalTitle"],
            customContent: box,
            icon: OneUiIconType.CleanFilter,
            iconBadgeColor: OneUiTheme.Secondary,
            positiveText: S["CloseBtn"]);
        dlg.DismissEvent += (_, _) => CancelInspectorDebounce();
    }

    void ShowLibraryFolderFilterDialog() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        int chosenMode = store.LibraryFolderFilterMode;
        List<string> chosenFolders = store.GetLibraryFilterFolders();
        Android.App.Dialog dlg = null;

        LinearLayout[] modeRows = new LinearLayout[3];
        TextView[] modeLabels = new TextView[3];
        string[] labels = [S["LibFoldersAll"], S["LibFoldersExcept"], S["LibFoldersOnly"]];
        TextView summaryTv = Label("", 11.5f, OneUiTheme.TextSub);

        void RefreshFolderFilterModalUi() {
            for (int i = 0; i < 3; i++) {
                bool sel = chosenMode == i;
                modeRows[i].Background = sel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 95), 14f, OneUiTheme.PrimaryHigh, 1.2f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                modeLabels[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSecondary);
            }
            string listPreview = chosenFolders.Count == 0
                ? "—"
                : string.Join("\n", chosenFolders.Take(6).Select(p => $"• {p}")) +
                  (chosenFolders.Count > 6 ? $"\n(+{chosenFolders.Count - 6})" : "");
            summaryTv.Text = $"{string.Format(S["LibFoldersCountBadge"], chosenFolders.Count, labels[Math.Clamp(chosenMode, 0, 2)])}\n{listPreview}";
        }

        for (int i = 0; i < 3; i++) {
            int m = i;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            OneUiTheme.AttachRecoil(row, 0.98f);
            TextView tv = Label(labels[i], 13f, OneUiTheme.TextSecondary);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                chosenMode = m;
                RefreshFolderFilterModalUi();
            };
            modeRows[i] = row;
            modeLabels[i] = tv;
            content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        summaryTv.SetPadding(Dp(4), Dp(6), Dp(4), Dp(10));
        content.AddView(summaryTv);

        Button pickFoldersBtn = PrimaryButton(S["LibFoldersPickBtn"], () => {
            OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
                Title = S["ExplorerTitleFolders"],
                Mode = ExplorerPickMode.MultiFolder,
                InitialDirectory = store.LastExplorerDirectory,
                InitiallySelectedPaths = chosenFolders,
                ConfirmLabel = S["ExplorerApplyFolders"],
                OnConfirm = picked => {
                    chosenFolders = picked?.ToList() ?? [];
                    if (chosenFolders.Count > 0 && chosenMode == 0) {
                        chosenMode = 2; // Auto-activate "Only Selected" when user explicitly selects folders
                    }
                    RefreshFolderFilterModalUi();
                }
            });
        });
        content.AddView(pickFoldersBtn);
        RefreshFolderFilterModalUi();

        dlg = OneUiModalDialog.Show(
            this,
            S["LibFoldersFilter"],
            customContent: content,
            icon: OneUiIconType.FolderFilter,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: "OK",
            onPositive: () => {
                store.LibraryFolderFilterMode = chosenFolders.Count == 0 ? 0 : chosenMode;
                store.SetLibraryFilterFolders(chosenFolders);
                selectedLibraryAlbum = null;
                selectedLibraryArtist = null;
                selectedLibraryFolder = null;
                libraryRenderLimit = 72;
                InvalidateLibraryIndexCache();
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            },
            negativeText: S["CancelBtn"]);
    }

    void UpdateLibraryCatStyles() {
        if (libCatTabs == null) return;
        LibraryCategory[] cats = [LibraryCategory.Albums, LibraryCategory.Tracks, LibraryCategory.Artists, LibraryCategory.Folders];
        for (int i = 0; i < libCatTabs.Length && i < cats.Length; i++) {
            bool sel = libraryCategory == cats[i];
            libCatTabs[i].Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : null;
            libCatTabs[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSub);
        }
    }

    void RefreshLibraryScanAsync(bool force) {
        if (isLibraryScanning) return;
        isLibraryScanning = true;
        isIncrementalSyncRunning = true;
        if (librarySnapshot == null && libraryStatusText != null) {
            libraryStatusText.Text = S["LibScanning"];
        }
        Task.Run(async () => {
            try {
                var (snap, added, updated, removed) = await MediaLibraryService.ScanIncrementalAsync(this, force).ConfigureAwait(false);
                RunOnUiThread(() => {
                    isLibraryScanning = false;
                    isIncrementalSyncRunning = false;
                    if (uiDestroyed) return;
                    bool firstLoad = librarySnapshot == null;
                    bool changed = firstLoad || force || added > 0 || updated > 0 || removed > 0;
                    librarySnapshot = snap;
                    InvalidateLibraryIndexCache();
                    UpdateLibraryFilterStyles();
                    if (changed || isLibrarySectionOpen) {
                        RenderLibraryView();
                        if ((added > 0 || updated > 0 || removed > 0) && libraryStatusText != null) {
                            int spatialCount = snap.Tracks.Count(t => t.IsSurround);
                            libraryStatusText.Text = string.Format(S["LibIncrementalUpdate"], snap.Tracks.Count, spatialCount, added + updated, removed);
                        }
                    }
                });
            } catch (Exception ex) {
                AppLogger.Error("RefreshLibraryScanAsync", ex);
                RunOnUiThread(() => {
                    isLibraryScanning = false;
                    isIncrementalSyncRunning = false;
                });
            }
        });
    }

    static void ComputeGroupSortMetrics(
        Context ctx,
        List<LibraryMergedTrack> merged,
        int sortMode,
        out long maxMod,
        out double maxScore,
        out int bestTier) {
        maxMod = 0;
        maxScore = 0;
        bestTier = 2;
        for (int i = 0; i < merged.Count; i++) {
            LibraryTrackItem p = merged[i].Primary;
            if (p == null) continue;
            if (p.LastModifiedTicks > maxMod) maxMod = p.LastModifiedTicks;
            int tier = p.IsSpatial ? 0 : (p.IsSurround ? 1 : 2);
            if (tier < bestTier) bestTier = tier;
            if (sortMode == 3) {
                double sc = AudioQualityAnalyzer.EvaluateFast(p, ctx, allowIo: false).Score;
                if (sc > maxScore) maxScore = sc;
            }
        }
    }

    sealed class LibraryBaseIndexData {
        public MediaLibrarySnapshot Snapshot;
        public int Revision;
        public int SpatialMode;
        public int MediaMode;
        public bool HideNoise;
        public int SortMode;
        public int FolderMode;
        public string FoldersJson = "[]";
        public List<LibraryTrackItem> VisibleTracks = [];
        public int VisibleSpatialCount;
        public AudioTierCategory? BuiltPrefCategory;
        public string BuiltPrefFormatKey = "";
        public List<SmartCleanInspectionEntry> SmartCleanEntries = [];
        public int HiddenNoiseCount;
        public List<LibraryMergedTrack> MergedTracks = [];
        public List<LibraryTrackItem> MergedTrackQueue = [];
        public List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> AlbumEntries = [];
        public List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> ArtistEntries = [];
        public List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> FolderEntries = [];
        public Dictionary<LibraryArtistGroup, (List<LibraryTrackItem> Queue, int DistinctAlbumCount)> ArtistMeta = [];
        public Dictionary<LibraryFolderGroup, List<LibraryTrackItem>> FolderQueues = [];
    }

    bool IsLibraryBaseIndexStale() {
        if (librarySnapshot == null || store == null) return false;
        int spatialMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
        int mediaMode = store.LibraryMediaTypeFilter;
        bool hideNoise = store.LibraryHideNonMusic;
        int sortMode = store.LibrarySortMode;
        int folderMode = store.LibraryFolderFilterMode;
        string foldersJson = store.LibraryFilterFoldersJson ?? "[]";

        return !(cachedLibIndexSnap == librarySnapshot &&
            cachedLibIndexRevision == libraryFilterRevision &&
            cachedLibSpatialMode == spatialMode &&
            cachedLibMediaMode == mediaMode &&
            cachedLibHideNoise == hideNoise &&
            cachedLibSortMode == sortMode &&
            cachedLibFolderMode == folderMode &&
            string.Equals(cachedLibFoldersJson, foldersJson, StringComparison.Ordinal));
    }

    void ScheduleLibraryLoadAndRenderAsync(bool loadCacheFromDisk) {
        if (uiDestroyed || store == null) return;
        int mySeq = Interlocked.Increment(ref libraryIndexRequestSeq);
        isLibraryIndexBuilding = true;
        if (libraryListHost != null && librarySnapshot == null) {
            libraryListHost.RemoveAllViews();
        }
        if (libraryStatusText != null) {
            libraryStatusText.Text = isLibraryScanning ? S["LibScanning"] : S["LibraryLoadingCached"];
        }

        Context appCtx = ApplicationContext ?? this;
        MediaLibrarySnapshot currentSnap = loadCacheFromDisk ? null : librarySnapshot;
        int rev = libraryFilterRevision;
        int spatialMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
        int mediaMode = store.LibraryMediaTypeFilter;
        bool hideNoise = store.LibraryHideNonMusic;
        int sortMode = store.LibrarySortMode;
        int folderMode = store.LibraryFolderFilterMode;
        string foldersJson = store.LibraryFilterFoldersJson ?? "[]";
        List<string> selectedFolders = store.GetLibraryFilterFolders();
        AudioTierCategory? prefCat = preferredAudioCategory;
        string prefFmtKey = preferredFormatVariantKey ?? "";

        _ = Task.Run(() => {
            MediaLibrarySnapshot loadedSnap = currentSnap;
            if (loadedSnap == null) {
                MediaLibrarySnapshot cached = MediaLibraryService.TryLoadCachedSnapshot(appCtx);
                if (cached != null && cached.Tracks.Count > 0) {
                    loadedSnap = cached;
                }
            }
            LibraryBaseIndexData built = loadedSnap != null
                ? BuildLibraryBaseIndexSnapshot(appCtx, loadedSnap, rev, spatialMode, mediaMode, hideNoise, sortMode, folderMode, foldersJson, selectedFolders, prefCat, prefFmtKey)
                : null;

            RunOnUiThread(() => {
                if (uiDestroyed || mySeq != Volatile.Read(ref libraryIndexRequestSeq)) {
                    return;
                }
                isLibraryIndexBuilding = false;
                if (loadedSnap != null) {
                    librarySnapshot = loadedSnap;
                }
                if (built != null) {
                    cachedVisibleTracks = built.VisibleTracks;
                    cachedVisibleSpatialCount = built.VisibleSpatialCount;
                    cachedBuiltPrefCategory = built.BuiltPrefCategory;
                    cachedBuiltPrefFormatKey = built.BuiltPrefFormatKey;
                    cachedSmartCleanEntries = built.SmartCleanEntries;
                    cachedHiddenNoiseCount = built.HiddenNoiseCount;
                    cachedMergedTracks = built.MergedTracks;
                    cachedMergedTrackQueue = built.MergedTrackQueue;
                    cachedAlbumEntries = built.AlbumEntries;
                    cachedArtistEntries = built.ArtistEntries;
                    cachedFolderEntries = built.FolderEntries;
                    cachedArtistMeta = built.ArtistMeta;
                    cachedFolderQueues = built.FolderQueues;
                    string baseBadge = string.Format(S["LibCacheSynced"], built.VisibleTracks.Count, built.VisibleSpatialCount);
                    cachedLibraryBadgeText = built.HiddenNoiseCount > 0
                        ? $"{baseBadge} • {string.Format(S["LibHiddenNoiseCount"], built.HiddenNoiseCount)}"
                        : baseBadge;
                    cachedLibIndexSnap = built.Snapshot;
                    cachedLibIndexRevision = built.Revision;
                    cachedLibSpatialMode = built.SpatialMode;
                    cachedLibMediaMode = built.MediaMode;
                    cachedLibHideNoise = built.HideNoise;
                    cachedLibSortMode = built.SortMode;
                    cachedLibFolderMode = built.FolderMode;
                    cachedLibFoldersJson = built.FoldersJson;
                    cachedQueryRevision = -1;
                    ClearLibraryTabViewCache();
                }
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            });
        });
    }

    static LibraryBaseIndexData BuildLibraryBaseIndexSnapshot(
        Context appCtx,
        MediaLibrarySnapshot snap,
        int rev,
        int spatialMode,
        int mediaMode,
        bool hideNoise,
        int sortMode,
        int folderMode,
        string foldersJson,
        List<string> selectedFolders,
        AudioTierCategory? prefCat,
        string prefFmtKey) {
        AppSettingsStore bgStore = new(appCtx);
        List<LibraryTrackItem> visible = MediaLibraryService.FilterTracks(snap.Tracks, bgStore);
        int visibleSpatialCount = 0;
        for (int i = 0; i < visible.Count; i++) {
            if (visible[i].IsSurround) {
                visibleSpatialCount++;
            }
        }

        List<LibraryTrackItem> scopeTracks = new(snap.Tracks.Count);
        for (int i = 0; i < snap.Tracks.Count; i++) {
            LibraryTrackItem t = snap.Tracks[i];
            if (t != null &&
                MediaLibraryService.PassesFilter(t, spatialMode) &&
                MediaLibraryService.PassesMediaTypeFilter(t, mediaMode) &&
                MediaLibraryService.PassesFolderFilter(t.FolderPath, folderMode, selectedFolders)) {
                scopeTracks.Add(t);
            }
        }
        List<SmartCleanInspectionEntry> cleanEntries = MediaLibraryService.GetSmartCleanInspectionList(scopeTracks, bgStore);
        int hiddenNoise = hideNoise
            ? cleanEntries.Count
            : cleanEntries.Count(e => e.Category == SmartCleanCategory.UserHiddenFolder);

        HashSet<LibraryTrackItem> visSet = new(visible);
        List<LibraryMergedTrack> mergedTracksRaw = MediaLibraryService.MergeTrackVariants(visible, appCtx);
        List<LibraryMergedTrack> sortedTracks = MediaLibraryService.SortMergedTracks(mergedTracksRaw, sortMode, appCtx);
        List<LibraryTrackItem> mergedTrackQueue = new(sortedTracks.Count);
        for (int i = 0; i < sortedTracks.Count; i++) {
            LibraryMergedTrack m = sortedTracks[i];
            LibraryTrackItem pref = MediaLibraryService.ResolvePreferredVariant(m, prefCat, prefFmtKey);
            m.PreferredVariant = pref;
            if (pref != null) {
                mergedTrackQueue.Add(pref);
            }
        }

        List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> albEntries = [];
        for (int i = 0; i < snap.Albums.Count; i++) {
            LibraryAlbumGroup alb = snap.Albums[i];
            List<LibraryTrackItem> vt = alb.Tracks.Where(visSet.Contains).ToList();
            if (vt.Count == 0) continue;
            List<LibraryMergedTrack> merged = MediaLibraryService.MergeAlbumTrackVariants(vt, appCtx);
            if (merged.Count == 0) continue;
            for (int j = 0; j < merged.Count; j++) {
                merged[j].PreferredVariant = MediaLibraryService.ResolvePreferredVariant(merged[j], prefCat, prefFmtKey);
            }
            ComputeGroupSortMetrics(appCtx, merged, sortMode, out long maxMod, out double maxScore, out int bestTier);
            albEntries.Add((alb, merged, maxMod, maxScore, bestTier));
        }
        albEntries.Sort((a, b) => sortMode switch {
            1 => string.Compare(a.Alb.Artist, b.Alb.Artist, StringComparison.OrdinalIgnoreCase) is int ac && ac != 0
                ? ac
                : string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase),
            2 => b.MaxModTicks.CompareTo(a.MaxModTicks) is int tc && tc != 0
                ? tc
                : string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase),
            3 => a.BestTier.CompareTo(b.BestTier) is int bc && bc != 0
                ? bc
                : (b.MaxScore.CompareTo(a.MaxScore) is int sc && sc != 0
                    ? sc
                    : string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase)),
            _ => string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase)
        });

        List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> artEntries = [];
        Dictionary<LibraryArtistGroup, (List<LibraryTrackItem> Queue, int DistinctAlbumCount)> artistMeta = new(snap.Artists.Count);
        for (int i = 0; i < snap.Artists.Count; i++) {
            LibraryArtistGroup art = snap.Artists[i];
            List<LibraryTrackItem> vt = art.Tracks.Where(visSet.Contains).ToList();
            if (vt.Count == 0) continue;
            List<LibraryMergedTrack> merged = MediaLibraryService.SortMergedTracks(
                MediaLibraryService.MergeTrackVariants(vt, appCtx), sortMode, appCtx);
            if (merged.Count == 0) continue;
            List<LibraryTrackItem> artQueue = new(merged.Count);
            HashSet<string> albums = new(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < merged.Count; j++) {
                LibraryMergedTrack m = merged[j];
                LibraryTrackItem pref = MediaLibraryService.ResolvePreferredVariant(m, prefCat, prefFmtKey);
                m.PreferredVariant = pref;
                if (pref != null) artQueue.Add(pref);
                string albKey = string.IsNullOrWhiteSpace(m.Primary.Album)
                    ? m.Primary.FolderPath
                    : m.Primary.Album;
                if (!string.IsNullOrEmpty(albKey)) albums.Add(albKey);
            }
            artistMeta[art] = (artQueue, Math.Max(1, albums.Count));
            ComputeGroupSortMetrics(appCtx, merged, sortMode, out long maxMod, out double maxScore, out int bestTier);
            artEntries.Add((art, merged, maxMod, maxScore, bestTier));
        }
        artEntries.Sort((a, b) => sortMode switch {
            2 => b.MaxModTicks.CompareTo(a.MaxModTicks) is int tc && tc != 0
                ? tc
                : string.Compare(a.Art.Artist, b.Art.Artist, StringComparison.OrdinalIgnoreCase),
            3 => a.BestTier.CompareTo(b.BestTier) is int bc && bc != 0
                ? bc
                : (b.MaxScore.CompareTo(a.MaxScore) is int sc && sc != 0
                    ? sc
                    : string.Compare(a.Art.Artist, b.Art.Artist, StringComparison.OrdinalIgnoreCase)),
            _ => string.Compare(a.Art.Artist, b.Art.Artist, StringComparison.OrdinalIgnoreCase)
        });

        List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> fldEntries = [];
        Dictionary<LibraryFolderGroup, List<LibraryTrackItem>> folderQueues = new(snap.Folders.Count);
        for (int i = 0; i < snap.Folders.Count; i++) {
            LibraryFolderGroup fld = snap.Folders[i];
            List<LibraryTrackItem> vt = fld.Tracks.Where(visSet.Contains).ToList();
            if (vt.Count == 0) continue;
            List<LibraryMergedTrack> merged = MediaLibraryService.SortMergedTracks(
                MediaLibraryService.MergeTrackVariants(vt, appCtx), sortMode, appCtx);
            if (merged.Count == 0) continue;
            List<LibraryTrackItem> fldQueue = new(merged.Count);
            for (int j = 0; j < merged.Count; j++) {
                LibraryMergedTrack m = merged[j];
                LibraryTrackItem pref = MediaLibraryService.ResolvePreferredVariant(m, prefCat, prefFmtKey);
                m.PreferredVariant = pref;
                if (pref != null) fldQueue.Add(pref);
            }
            folderQueues[fld] = fldQueue;
            ComputeGroupSortMetrics(appCtx, merged, sortMode, out long maxMod, out double maxScore, out int bestTier);
            fldEntries.Add((fld, merged, maxMod, maxScore, bestTier));
        }
        fldEntries.Sort((a, b) => sortMode switch {
            2 => b.MaxModTicks.CompareTo(a.MaxModTicks) is int tc && tc != 0
                ? tc
                : string.Compare(a.Fld.FolderName, b.Fld.FolderName, StringComparison.OrdinalIgnoreCase),
            3 => a.BestTier.CompareTo(b.BestTier) is int bc && bc != 0
                ? bc
                : (b.MaxScore.CompareTo(a.MaxScore) is int sc && sc != 0
                    ? sc
                    : string.Compare(a.Fld.FolderName, b.Fld.FolderName, StringComparison.OrdinalIgnoreCase)),
            _ => string.Compare(a.Fld.FolderName, b.Fld.FolderName, StringComparison.OrdinalIgnoreCase)
        });

        return new LibraryBaseIndexData {
            Snapshot = snap,
            Revision = rev,
            SpatialMode = spatialMode,
            MediaMode = mediaMode,
            HideNoise = hideNoise,
            SortMode = sortMode,
            FolderMode = folderMode,
            FoldersJson = foldersJson,
            VisibleTracks = visible,
            VisibleSpatialCount = visibleSpatialCount,
            BuiltPrefCategory = prefCat,
            BuiltPrefFormatKey = prefFmtKey,
            SmartCleanEntries = cleanEntries,
            HiddenNoiseCount = hiddenNoise,
            MergedTracks = sortedTracks,
            MergedTrackQueue = mergedTrackQueue,
            AlbumEntries = albEntries,
            ArtistEntries = artEntries,
            FolderEntries = fldEntries,
            ArtistMeta = artistMeta,
            FolderQueues = folderQueues
        };
    }

    internal static string NormalizeSearchFold(string text) {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string decomposed = text.Trim().Normalize(System.Text.NormalizationForm.FormD);
        System.Text.StringBuilder sb = new(decomposed.Length);
        for (int i = 0; i < decomposed.Length; i++) {
            char c = decomposed[i];
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    internal static bool MatchesSearchQuery(string haystack, string normalizedQuery) {
        if (string.IsNullOrEmpty(normalizedQuery)) return true;
        if (string.IsNullOrEmpty(haystack)) return false;
        if (haystack.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return true;
        return NormalizeSearchFold(haystack).Contains(normalizedQuery, StringComparison.Ordinal);
    }

    internal static bool ShouldFilterDrilldownTracks(string rawQuery, params string[] containerLabels) {
        string normQ = NormalizeSearchFold(rawQuery);
        if (string.IsNullOrEmpty(normQ)) return false;
        if (containerLabels != null) {
            for (int i = 0; i < containerLabels.Length; i++) {
                if (MatchesSearchQuery(containerLabels[i], normQ)) {
                    return false;
                }
            }
        }
        return true;
    }

    void EnsureLibraryQueryCache(string q, LibraryCategory cat) {
        if (cachedQuerySnap == librarySnapshot &&
            cachedQueryRevision == libraryFilterRevision &&
            cachedQueryCat == cat &&
            string.Equals(cachedQueryStr, q, StringComparison.Ordinal)) {
            return;
        }
        string normQ = NormalizeSearchFold(q);
        bool hasQuery = !string.IsNullOrEmpty(normQ);
        if (cat == LibraryCategory.Albums) {
            cachedQueryAlbums = cachedAlbumEntries
                .Where(x => !hasQuery ||
                    MatchesSearchQuery(x.Alb.AlbumTitle, normQ) ||
                    MatchesSearchQuery(x.Alb.Artist, normQ) ||
                    x.Merged.Any(m => MatchesSearchQuery(m.Primary.Title, normQ) || MatchesSearchQuery(m.Primary.Artist, normQ)))
                .Select(x => (x.Alb, x.Merged))
                .ToList();
        } else if (cat == LibraryCategory.Tracks) {
            cachedQueryMergedTracks = hasQuery
                ? cachedMergedTracks.Where(m =>
                    MatchesSearchQuery(m.Primary.Title, normQ) ||
                    MatchesSearchQuery(m.Primary.Artist, normQ) ||
                    MatchesSearchQuery(m.Primary.AlbumArtist, normQ) ||
                    MatchesSearchQuery(m.Primary.Album, normQ) ||
                    MatchesSearchQuery(m.Primary.FolderName, normQ)).ToList()
                : cachedMergedTracks;
        } else if (cat == LibraryCategory.Artists) {
            cachedQueryArtists = cachedArtistEntries
                .Where(x => !hasQuery ||
                    MatchesSearchQuery(x.Art.Artist, normQ) ||
                    x.Merged.Any(m => MatchesSearchQuery(m.Primary.Title, normQ) || MatchesSearchQuery(m.Primary.Album, normQ)))
                .Select(x => (x.Art, x.Merged))
                .ToList();
        } else {
            cachedQueryFolders = cachedFolderEntries
                .Where(x => !hasQuery ||
                    MatchesSearchQuery(x.Fld.FolderName, normQ) ||
                    MatchesSearchQuery(x.Fld.FolderPath, normQ) ||
                    x.Merged.Any(m => MatchesSearchQuery(m.Primary.Title, normQ) || MatchesSearchQuery(m.Primary.Album, normQ)))
                .Select(x => (x.Fld, x.Merged))
                .ToList();
        }
        cachedQuerySnap = librarySnapshot;
        cachedQueryRevision = libraryFilterRevision;
        cachedQueryCat = cat;
        cachedQueryStr = q;
    }

    void SyncSmartCleanChipBadge() {
        if (libNoiseToggleBtn == null || libNoiseToggleText == null || libNoiseToggleIcon == null) return;
        bool on = store.LibraryHideNonMusic;
        int hiddenCount = librarySnapshot != null ? cachedHiddenNoiseCount : 0;
        libNoiseToggleText.Text = hiddenCount > 0
            ? string.Format(S["SmartCleanChipBadge"], hiddenCount)
            : S["LibHideNoise"];
        libNoiseToggleBtn.Background = on
            ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Secondary, 115), OneUiTheme.PillCornerDp, OneUiTheme.Secondary, 1f)
            : (hiddenCount > 0
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalAmber, 70), OneUiTheme.PillCornerDp, OneUiTheme.FunctionalAmber, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp));
        Color fg = on || hiddenCount > 0 ? Color.White : OneUiTheme.TextSub;
        libNoiseToggleIcon.IconColor = fg;
        libNoiseToggleText.SetTextColor(fg);
    }

}
