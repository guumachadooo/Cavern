using Android.Animation;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
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
    sealed class CachedLibraryTabView {
        public LinearLayout Container;
        public MediaLibrarySnapshot Snap;
        public int Revision;
        public string Query = "";
        public int Limit;
        public string LoadedPath = "";
        public AudioTierCategory? PrefCategory;
        public string PrefFormatKey = "";
        public int ItemCount;
        public bool IsComplete;
    }

    readonly CachedLibraryTabView[] cachedLibraryTabViews = new CachedLibraryTabView[4];
    Drawable.ConstantState libTrackRowBgState;
    Drawable.ConstantState libTrackRowPlayingBgState;
    Drawable.ConstantState libGroupCardBgState;
    Drawable.ConstantState libBadgeSpatialBgState;
    Drawable.ConstantState libBadgeNormalBgState;
    Drawable.ConstantState libPillPrimaryBgState;
    Drawable.ConstantState libPillDangerBgState;
    Drawable.ConstantState libPillNormalBgState;

    void ClearLibraryTabViewCache() {
        for (int i = 0; i < cachedLibraryTabViews.Length; i++) {
            cachedLibraryTabViews[i] = null;
        }
    }

    Drawable CloneCachedDrawable(ref Drawable.ConstantState slot, Func<Drawable> factory) {
        if (slot == null) {
            Drawable proto = factory();
            slot = proto.GetConstantState();
            return proto;
        }
        return slot.NewDrawable(Resources) ?? factory();
    }

    Drawable GetCachedTrackRowDrawable(bool isPlaying) => isPlaying
        ? CloneCachedDrawable(ref libTrackRowPlayingBgState,
            () => OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 64), 16f, ColAccent, 1.1f))
        : CloneCachedDrawable(ref libTrackRowBgState,
            () => OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceSubTile, 16f, OneUiTheme.GlassRimMid, 1f));

    Drawable GetCachedGroupCardDrawable() => CloneCachedDrawable(
        ref libGroupCardBgState,
        () => OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceCard, 18f, OneUiTheme.GlassRimTop, 1f));

    Drawable GetCachedBadgeDrawable(bool anySpatial) => anySpatial
        ? CloneCachedDrawable(ref libBadgeSpatialBgState,
            () => OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 52), 6f))
        : CloneCachedDrawable(ref libBadgeNormalBgState,
            () => OneUiTheme.CreateRoundedDrawable(this, Color.Argb(40, 255, 255, 255), 6f));

    Drawable GetCachedPillDrawable(bool primary, bool subtleDanger) {
        if (primary) {
            return CloneCachedDrawable(ref libPillPrimaryBgState,
                () => OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp));
        }
        if (subtleDanger) {
            return CloneCachedDrawable(ref libPillDangerBgState,
                () => OneUiTheme.CreateRoundedDrawable(
                    this,
                    OneUiTheme.WithAlpha(OneUiTheme.FunctionalRedFill, 42),
                    OneUiTheme.PillCornerDp,
                    OneUiTheme.WithAlpha(OneUiTheme.FunctionalRed, 130),
                    0.9f));
        }
        return CloneCachedDrawable(ref libPillNormalBgState,
            () => OneUiTheme.CreateRoundedDrawable(
                this,
                OneUiTheme.SurfaceSubTile,
                OneUiTheme.PillCornerDp,
                Color.Argb(40, 255, 255, 255),
                0.9f));
    }

    void PlayLibraryQueueShuffled(IReadOnlyList<LibraryTrackItem> queue) {
        if (queue == null || queue.Count == 0) return;
        List<LibraryTrackItem> shuffled = queue.Where(t => t != null).ToList();
        if (shuffled.Count == 0) return;
        for (int i = shuffled.Count - 1; i > 0; i--) {
            int j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        OpenLibraryTrack(shuffled[0], shuffled, autoPlay: true);
    }

    void HideFolderFromLibrary(LibraryFolderGroup fld) {
        if (fld == null || string.IsNullOrWhiteSpace(fld.FolderPath)) return;
        store.HideLibraryFolder(fld.FolderPath);
        if (selectedLibraryFolder == fld) {
            selectedLibraryFolder = null;
        }
        libraryRenderLimit = 72;
        InvalidateLibraryIndexCache();
        UpdateLibraryFilterStyles();
        RenderLibraryView();
        Toast.MakeText(this, string.Format(S["LibFolderHiddenToast"], fld.FolderName), ToastLength.Short).Show();
    }

    LinearLayout BuildGroupActionPill(
        OneUiIconType? icon,
        string label,
        Action onClick,
        bool primary = false,
        bool subtleDanger = false) {
        LinearLayout pill = HRow();
        pill.SetGravity(GravityFlags.Center);
        pill.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        pill.SetMinimumHeight(Dp(30));
        pill.Background = GetCachedPillDrawable(primary, subtleDanger);
        OneUiTheme.AttachRecoil(pill, 0.94f);

        Color fg = subtleDanger ? OneUiTheme.FunctionalRed : ColText;
        if (icon.HasValue) {
            OneUiIconView ico = new(this, icon.Value, fg);
            pill.AddView(ico, new LinearLayout.LayoutParams(Dp(13), Dp(13)) {
                RightMargin = string.IsNullOrEmpty(label) ? 0 : Dp(5)
            });
        }
        if (!string.IsNullOrEmpty(label)) {
            TextView tv = Label(label, 11f, fg);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
            tv.SetIncludeFontPadding(false);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            pill.AddView(tv);
        }
        pill.Click += (_, _) => onClick?.Invoke();
        return pill;
    }

    LinearLayout BuildFoldersToolbar() {
        LinearLayout bar = HRow();
        bar.SetPadding(0, 0, 0, Dp(8));
        List<string> dirs = store.GetLibraryFilterFolders();
        bool scopeActive = store.LibraryFolderFilterMode > 0 && dirs.Count > 0;
        string scopeTxt = scopeActive ? string.Format(S["LibFoldersChipActive"], dirs.Count) : S["LibFoldersFilter"];
        LinearLayout scopePill = BuildGroupActionPill(OneUiIconType.FolderFilter, scopeTxt, ShowLibraryFolderFilterDialog, primary: scopeActive);
        LinearLayout explorerPill = BuildGroupActionPill(OneUiIconType.Folder, S["LibOpenExplorer"], () => OpenMediaWithExplorer(switchToTab: 0));
        bar.AddView(scopePill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        bar.AddView(explorerPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return bar;
    }

    void ScheduleLibraryIncrementalRender(
        int myRenderGen,
        int startIndex,
        int endIndex,
        int step,
        int incrementalBatchItems,
        Action<int> appendItem,
        Action onDone) {
        int cursor = startIndex;
        if (cursor >= endIndex) {
            onDone?.Invoke();
            return;
        }
        activeLibraryPumpRunnable = new Java.Lang.Runnable(() => {
            if (uiDestroyed || myRenderGen != libraryRenderGeneration || libraryListHost == null) {
                return;
            }
            int batchEnd = Math.Min(endIndex, cursor + incrementalBatchItems);
            for (int idx = cursor; idx < batchEnd; idx += step) {
                appendItem(idx);
            }
            cursor = batchEnd;
            if (cursor < endIndex) {
                libraryListHost.PostOnAnimation(activeLibraryPumpRunnable);
            } else {
                activeLibraryPumpRunnable = null;
                onDone?.Invoke();
            }
        });
        libraryListHost.PostOnAnimation(activeLibraryPumpRunnable);
    }

    string FormatRootTabStatusText(LibraryCategory cat, int count, string cacheTag) => cat switch {
        LibraryCategory.Albums => $"{count} {S["LibAlbums"].ToLowerInvariant()} • {cacheTag}",
        LibraryCategory.Tracks => $"{string.Format(S["LibSongsCount"], count)} • {cacheTag}",
        LibraryCategory.Artists => $"{count} {S["LibArtists"].ToLowerInvariant()} • {cacheTag}",
        _ => $"{count} {S["LibFolders"].ToLowerInvariant()} • {cacheTag}"
    };

    void RenderLibraryView() {
        if (libraryListHost == null) return;
        CancelLibraryRenderPump();
        int myRenderGen = ++libraryRenderGeneration;
        libraryListHost.RemoveAllViews();

        if (librarySnapshot == null) {
            libraryStatusText.Text = isLibraryScanning
                ? S["LibScanning"]
                : (isLibraryIndexBuilding ? S["LibraryLoadingCached"] : S["LibEmpty"]);
            return;
        }

        if (IsLibraryBaseIndexStale()) {
            ScheduleLibraryLoadAndRenderAsync(loadCacheFromDisk: false);
            return;
        }
        SyncSmartCleanChipBadge();

        string q = (librarySearchQuery ?? "").Trim();
        bool hasQuery = !string.IsNullOrEmpty(q);
        string cacheTag = isIncrementalSyncRunning
            ? S["LibSyncing"]
            : (!string.IsNullOrEmpty(cachedLibraryBadgeText)
                ? cachedLibraryBadgeText
                : string.Format(S["LibCacheSynced"], cachedVisibleTracks.Count, cachedVisibleSpatialCount));

        bool hasAnyActiveFilter = store.LibraryFilterMode > 0
            || store.LibraryMediaTypeFilter > 0
            || store.LibraryFolderFilterMode > 0
            || store.SmartCleanHiddenFolders.Count > 0
            || store.LibraryHideNonMusic;

        // Detail Drill-Down inside Album / Artist / Folder (all merged via MergeTrackVariants + paginated)
        if (selectedLibraryAlbum != null) {
            RenderAlbumDetailView(selectedLibraryAlbum, q, myRenderGen);
            return;
        }
        if (selectedLibraryFolder != null) {
            var fldMatch = cachedFolderEntries.FirstOrDefault(f => f.Fld == selectedLibraryFolder ||
                string.Equals(f.Fld.FolderPath, selectedLibraryFolder.FolderPath, StringComparison.OrdinalIgnoreCase));
            List<LibraryMergedTrack> fldMerged = fldMatch.Fld != null
                ? fldMatch.Merged
                : MediaLibraryService.SortMergedTracks(
                    MediaLibraryService.MergeTrackVariants(FilterLibraryTracks(selectedLibraryFolder.Tracks), this),
                    store.LibrarySortMode, this);
            if (ShouldFilterDrilldownTracks(q, selectedLibraryFolder.FolderName, selectedLibraryFolder.FolderPath)) {
                string normQ = NormalizeSearchFold(q);
                List<LibraryMergedTrack> narrowed = fldMerged.Where(m =>
                    MatchesSearchQuery(m.Primary.Title, normQ) ||
                    MatchesSearchQuery(m.Primary.Artist, normQ) ||
                    MatchesSearchQuery(m.Primary.Album, normQ)).ToList();
                if (narrowed.Count > 0) {
                    fldMerged = narrowed;
                }
            }
            LibraryFolderGroup capturedFld = selectedLibraryFolder;
            RenderGroupTracksDrilldown(
                selectedLibraryFolder.FolderName,
                $"{string.Format(S["LibSongsCount"], fldMerged.Count)} • {selectedLibraryFolder.FolderPath}",
                fldMerged,
                () => { selectedLibraryFolder = null; RenderLibraryView(); },
                onHideFolder: () => HideFolderFromLibrary(capturedFld),
                myRenderGen: myRenderGen);
            return;
        }
        if (!string.IsNullOrEmpty(selectedLibraryArtist)) {
            var artMatch = cachedArtistEntries.FirstOrDefault(a =>
                string.Equals(a.Art.Artist, selectedLibraryArtist, StringComparison.OrdinalIgnoreCase));
            LibraryArtistGroup art = artMatch.Art ?? librarySnapshot.Artists.FirstOrDefault(a =>
                string.Equals(a.Artist, selectedLibraryArtist, StringComparison.OrdinalIgnoreCase));
            if (art != null) {
                List<LibraryMergedTrack> artMerged = artMatch.Art != null
                    ? artMatch.Merged
                    : MediaLibraryService.SortMergedTracks(
                        MediaLibraryService.MergeTrackVariants(FilterLibraryTracks(art.Tracks), this),
                        store.LibrarySortMode, this);
                if (ShouldFilterDrilldownTracks(q, art.Artist)) {
                    string normQ = NormalizeSearchFold(q);
                    List<LibraryMergedTrack> narrowed = artMerged.Where(m =>
                        MatchesSearchQuery(m.Primary.Title, normQ) ||
                        MatchesSearchQuery(m.Primary.Album, normQ)).ToList();
                    if (narrowed.Count > 0) {
                        artMerged = narrowed;
                    }
                }
                RenderGroupTracksDrilldown(
                    art.Artist,
                    string.Format(S["LibSongsCount"], artMerged.Count),
                    artMerged,
                    () => { selectedLibraryArtist = null; RenderLibraryView(); },
                    myRenderGen: myRenderGen);
                return;
            }
        }

        int tabIdx = Math.Clamp((int)libraryCategory, 0, cachedLibraryTabViews.Length - 1);
        string loadedPath = LoadedFile?.Path ?? "";
        string prefFmt = preferredFormatVariantKey ?? "";
        bool prefsMatchIndex = cachedBuiltPrefCategory == preferredAudioCategory &&
            string.Equals(cachedBuiltPrefFormatKey, prefFmt, StringComparison.OrdinalIgnoreCase);

        CachedLibraryTabView cachedTab = cachedLibraryTabViews[tabIdx];
        if (cachedTab != null &&
            cachedTab.IsComplete &&
            cachedTab.Container != null &&
            cachedTab.Snap == librarySnapshot &&
            cachedTab.Revision == libraryFilterRevision &&
            cachedTab.Limit == libraryRenderLimit &&
            cachedTab.PrefCategory == preferredAudioCategory &&
            string.Equals(cachedTab.PrefFormatKey, prefFmt, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cachedTab.Query, q, StringComparison.Ordinal) &&
            (libraryCategory != LibraryCategory.Tracks || string.Equals(cachedTab.LoadedPath, loadedPath, StringComparison.OrdinalIgnoreCase))) {
            libraryStatusText.Text = FormatRootTabStatusText(libraryCategory, cachedTab.ItemCount, cacheTag);
            if (cachedTab.Container.Parent is ViewGroup oldParent) {
                oldParent.RemoveView(cachedTab.Container);
            }
            libraryListHost.AddView(cachedTab.Container, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            return;
        }

        EnsureLibraryQueryCache(q, libraryCategory);

        const int InitialBatchItems = 6;
        const int IncrementalBatchItems = 6;

        LinearLayout tabContainer = new(this) { Orientation = Orientation.Vertical };
        libraryListHost.AddView(tabContainer, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        CachedLibraryTabView tabCacheEntry = new() {
            Container = tabContainer,
            Snap = librarySnapshot,
            Revision = libraryFilterRevision,
            Query = q,
            Limit = libraryRenderLimit,
            LoadedPath = loadedPath,
            PrefCategory = preferredAudioCategory,
            PrefFormatKey = prefFmt,
            IsComplete = false
        };
        cachedLibraryTabViews[tabIdx] = tabCacheEntry;

        if (libraryCategory == LibraryCategory.Albums) {
            List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged)> filtered = cachedQueryAlbums;
            tabCacheEntry.ItemCount = filtered.Count;
            libraryStatusText.Text = FormatRootTabStatusText(LibraryCategory.Albums, filtered.Count, cacheTag);
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0, tabContainer);
                tabCacheEntry.IsComplete = true;
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            void AppendAlbumPair(int i) {
                LinearLayout gridRow = new(this) {
                    Orientation = Orientation.Horizontal,
                    BaselineAligned = false
                };
                gridRow.AddView(BuildAlbumTileCard(filtered[i].Alb, filtered[i].Merged), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                if (i + 1 < showCount) {
                    gridRow.AddView(BuildAlbumTileCard(filtered[i + 1].Alb, filtered[i + 1].Merged), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                } else {
                    gridRow.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1f));
                }
                tabContainer.AddView(gridRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            }

            int firstEnd = Math.Min(showCount, InitialBatchItems);
            for (int i = 0; i < firstEnd; i += 2) {
                AppendAlbumPair(i);
            }
            ScheduleLibraryIncrementalRender(myRenderGen, firstEnd, showCount, 2, IncrementalBatchItems, AppendAlbumPair, () => {
                AddLoadMoreIfNeeded(filtered.Count, showCount, tabContainer);
                tabCacheEntry.IsComplete = true;
            });
        } else if (libraryCategory == LibraryCategory.Tracks) {
            List<LibraryMergedTrack> filtered = cachedQueryMergedTracks;
            tabCacheEntry.ItemCount = filtered.Count;
            libraryStatusText.Text = FormatRootTabStatusText(LibraryCategory.Tracks, filtered.Count, cacheTag);
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0, tabContainer);
                tabCacheEntry.IsComplete = true;
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            List<LibraryTrackItem> queue;
            if (!hasQuery && prefsMatchIndex && cachedMergedTrackQueue.Count == filtered.Count) {
                queue = cachedMergedTrackQueue;
            } else {
                queue = new List<LibraryTrackItem>(showCount);
                for (int i = 0; i < showCount; i++) {
                    LibraryTrackItem chosen = PickPreferredVariant(filtered[i]);
                    if (chosen != null) {
                        queue.Add(chosen);
                    }
                }
            }
            int renderCount = Math.Min(showCount, queue.Count);
            void AppendTrackRow(int i) {
                LibraryMergedTrack m = filtered[i];
                LibraryTrackItem chosen = queue[i];
                tabContainer.AddView(
                    BuildLibraryTrackRow(
                        chosen,
                        queue,
                        i,
                        showThumb: true,
                        badgeOverride: m.Variants.Count > 1 ? m.CombinedBadgeLabel : null,
                        allVariantsForHighlight: m.Variants),
                    new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        BottomMargin = Dp(6)
                    });
            }

            int firstEnd = Math.Min(renderCount, InitialBatchItems);
            for (int i = 0; i < firstEnd; i++) {
                AppendTrackRow(i);
            }
            ScheduleLibraryIncrementalRender(myRenderGen, firstEnd, renderCount, 1, IncrementalBatchItems, AppendTrackRow, () => {
                AddLoadMoreIfNeeded(filtered.Count, renderCount, tabContainer);
                tabCacheEntry.IsComplete = true;
            });
        } else if (libraryCategory == LibraryCategory.Artists) {
            List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged)> filtered = cachedQueryArtists;
            tabCacheEntry.ItemCount = filtered.Count;
            libraryStatusText.Text = FormatRootTabStatusText(LibraryCategory.Artists, filtered.Count, cacheTag);
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0, tabContainer);
                tabCacheEntry.IsComplete = true;
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            void AppendArtistRow(int i) {
                LibraryArtistGroup art = filtered[i].Art;
                List<LibraryMergedTrack> merged = filtered[i].Merged;
                List<LibraryTrackItem> artQueue;
                int albumCnt;
                if (cachedArtistMeta.TryGetValue(art, out var meta) && meta.Queue != null && prefsMatchIndex) {
                    artQueue = meta.Queue;
                    albumCnt = meta.DistinctAlbumCount;
                } else {
                    artQueue = new List<LibraryTrackItem>(merged.Count);
                    HashSet<string> albums = meta.DistinctAlbumCount > 0 ? null : new(StringComparer.OrdinalIgnoreCase);
                    for (int j = 0; j < merged.Count; j++) {
                        LibraryTrackItem pref = PickPreferredVariant(merged[j]);
                        if (pref != null) artQueue.Add(pref);
                        if (albums != null) {
                            string albKey = string.IsNullOrWhiteSpace(merged[j].Primary.Album)
                                ? merged[j].Primary.FolderPath
                                : merged[j].Primary.Album;
                            if (!string.IsNullOrEmpty(albKey)) albums.Add(albKey);
                        }
                    }
                    albumCnt = meta.DistinctAlbumCount > 0 ? meta.DistinctAlbumCount : Math.Max(1, albums.Count);
                }
                LinearLayout row = BuildGroupRow(
                    art.Artist,
                    $"{string.Format(S["LibAlbumsCount"], albumCnt)} • {string.Format(S["LibSongsCount"], merged.Count)}",
                    artQueue.Count > 0 ? artQueue[0] : null,
                    artQueue,
                    () => {
                        selectedLibraryArtist = art.Artist;
                        libraryRenderLimit = 72;
                        RenderLibraryView();
                    });
                tabContainer.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }

            int firstEnd = Math.Min(showCount, InitialBatchItems);
            for (int i = 0; i < firstEnd; i++) {
                AppendArtistRow(i);
            }
            ScheduleLibraryIncrementalRender(myRenderGen, firstEnd, showCount, 1, IncrementalBatchItems, AppendArtistRow, () => {
                AddLoadMoreIfNeeded(filtered.Count, showCount, tabContainer);
                tabCacheEntry.IsComplete = true;
            });
        } else {
            tabContainer.AddView(BuildFoldersToolbar());
            List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged)> filtered = cachedQueryFolders;
            tabCacheEntry.ItemCount = filtered.Count;
            libraryStatusText.Text = FormatRootTabStatusText(LibraryCategory.Folders, filtered.Count, cacheTag);
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0, tabContainer);
                tabCacheEntry.IsComplete = true;
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            void AppendFolderRow(int i) {
                LibraryFolderGroup fld = filtered[i].Fld;
                List<LibraryMergedTrack> merged = filtered[i].Merged;
                List<LibraryTrackItem> fldQueue;
                if (cachedFolderQueues.TryGetValue(fld, out var preQueue) && preQueue != null && prefsMatchIndex) {
                    fldQueue = preQueue;
                } else {
                    fldQueue = new List<LibraryTrackItem>(merged.Count);
                    for (int j = 0; j < merged.Count; j++) {
                        LibraryTrackItem pref = PickPreferredVariant(merged[j]);
                        if (pref != null) fldQueue.Add(pref);
                    }
                }
                LibraryFolderGroup capturedFld = fld;
                LinearLayout row = BuildGroupRow(
                    fld.FolderName,
                    $"{string.Format(S["LibSongsCount"], merged.Count)} • {fld.FolderPath}",
                    fldQueue.Count > 0 ? fldQueue[0] : null,
                    fldQueue,
                    () => {
                        selectedLibraryFolder = capturedFld;
                        libraryRenderLimit = 72;
                        RenderLibraryView();
                    },
                    isFolder: true,
                    onHideFolder: () => HideFolderFromLibrary(capturedFld));
                tabContainer.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }

            int firstEnd = Math.Min(showCount, InitialBatchItems);
            for (int i = 0; i < firstEnd; i++) {
                AppendFolderRow(i);
            }
            ScheduleLibraryIncrementalRender(myRenderGen, firstEnd, showCount, 1, IncrementalBatchItems, AppendFolderRow, () => {
                AddLoadMoreIfNeeded(filtered.Count, showCount, tabContainer);
                tabCacheEntry.IsComplete = true;
            });
        }
    }

    void AddLoadMoreIfNeeded(int totalCount, int shown, LinearLayout targetHost = null) {
        if (shown >= totalCount) return;
        Button more = PillButton(string.Format(S["LibShowMore"], totalCount - shown), () => {
            libraryRenderLimit += 72;
            RenderLibraryView();
        });
        (targetHost ?? libraryListHost).AddView(more, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6)
        });
    }

    void RenderLibraryEmptyCard(bool isFilteredEmpty = false, LinearLayout targetHost = null) {
        LinearLayout card = Card();
        TextView tv = Label(isFilteredEmpty ? S["LibEmptyFilteredMedia"] : S["LibEmpty"], 12.5f, OneUiTheme.TextSub);
        tv.SetPadding(0, 0, 0, Dp(10));
        card.AddView(tv);
        if (isFilteredEmpty) {
            LinearLayout btns = HRow();
            Button showAllBtn = PrimaryButton(S["LibResetFiltersBtn"], () => {
                store.LibraryFilterMode = 0;
                store.LibraryMediaTypeFilter = 0;
                store.LibraryFolderFilterMode = 0;
                InvalidateLibraryIndexCache();
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            });
            btns.AddView(showAllBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (cachedHiddenNoiseCount > 0) {
                Button inspectBtn = PillButton(string.Format(S["LibInspectSmartCleanBtn"], cachedHiddenNoiseCount), ShowSmartCleanInspectorModal);
                btns.AddView(inspectBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = Dp(8) });
            }
            card.AddView(btns);
        } else {
            Button openPicker = PrimaryButton(S["LibOpenExplorer"], () => OpenMediaWithExplorer(switchToTab: 0));
            card.AddView(openPicker);
        }
        (targetHost ?? libraryListHost).AddView(card);
    }

    LinearLayout BuildAlbumTileCard(LibraryAlbumGroup alb, List<LibraryMergedTrack> visibleMerged) {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfReg = OneUiTheme.GetAppleFontRegular(this);

        LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
        tile.Background = GetCachedGroupCardDrawable();
        tile.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
        OneUiTheme.AttachRecoil(tile, 0.96f);

        LibraryTrackItem thumbSample = (visibleMerged != null && visibleMerged.Count > 0)
            ? visibleMerged[0].Primary
            : (alb.Tracks.Count > 0 ? alb.Tracks[0] : null);
        int trackCount = visibleMerged?.Count ?? alb.Tracks.Count;

        RoundedArtworkView art = new(this) {
            PreferredSizeDp = 148,
            CornerDp = 12f
        };
        BindThumbAsync(art, thumbSample, alb.AlbumTitle);
        tile.AddView(art, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView titleTv = Label(alb.AlbumTitle, 13f, ColText);
        titleTv.SetTypeface(sfBold, TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(titleTv);

        TextView subTv = Label($"{alb.Artist} • {trackCount}", 11f, OneUiTheme.TextMuted);
        subTv.SetTypeface(sfReg, TypefaceStyle.Normal);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(subTv);

        tile.Click += (_, _) => {
            selectedLibraryAlbum = alb;
            libraryRenderLimit = 72;
            RenderLibraryView();
        };
        return tile;
    }

    LibraryTrackItem PickPreferredVariant(LibraryMergedTrack m) {
        if (m == null) return null;
        if (m.PreferredVariant != null &&
            cachedBuiltPrefCategory == preferredAudioCategory &&
            string.Equals(cachedBuiltPrefFormatKey, preferredFormatVariantKey ?? "", StringComparison.OrdinalIgnoreCase)) {
            return m.PreferredVariant;
        }
        return MediaLibraryService.ResolvePreferredVariant(m, preferredAudioCategory, preferredFormatVariantKey);
    }

    void RenderAlbumDetailView(LibraryAlbumGroup alb, string q = "", int myRenderGen = 0) {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        var albMatch = cachedAlbumEntries.FirstOrDefault(a => a.Alb == alb ||
            string.Equals(a.Alb.Key, alb.Key, StringComparison.OrdinalIgnoreCase));
        List<LibraryMergedTrack> merged;
        if (albMatch.Alb != null) {
            merged = albMatch.Merged;
        } else {
            List<LibraryTrackItem> filteredRaw = FilterLibraryTracks(alb.Tracks);
            if (filteredRaw.Count == 0 &&
                store.LibraryFilterMode == 0 &&
                store.LibraryMediaTypeFilter == 0 &&
                store.LibraryFolderFilterMode == 0 &&
                store.SmartCleanHiddenFolders.Count == 0 &&
                !store.LibraryHideNonMusic) {
                filteredRaw = alb.Tracks;
            }
            merged = MediaLibraryService.MergeAlbumTrackVariants(filteredRaw, this);
        }
        if (ShouldFilterDrilldownTracks(q, alb.AlbumTitle, alb.Artist)) {
            string normQ = NormalizeSearchFold(q);
            List<LibraryMergedTrack> narrowed = merged.Where(m =>
                MatchesSearchQuery(m.Primary.Title, normQ) ||
                MatchesSearchQuery(m.Primary.Artist, normQ)).ToList();
            if (narrowed.Count > 0) {
                merged = narrowed;
            }
        }
        List<LibraryTrackItem> albumQueue = new(merged.Count);
        for (int i = 0; i < merged.Count; i++) {
            LibraryTrackItem pref = PickPreferredVariant(merged[i]);
            if (pref != null) albumQueue.Add(pref);
        }

        libraryStatusText.Text = $"{alb.AlbumTitle} — {string.Format(S["LibSongsCount"], merged.Count)}";

        LinearLayout hdrCard = Card();
        LinearLayout backRow = HRow();
        backRow.SetPadding(0, 0, 0, Dp(10));
        LinearLayout backPill = OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.ChevronLeft, S["LibAlbums"], () => {
            selectedLibraryAlbum = null;
            libraryRenderLimit = 72;
            RenderLibraryView();
        });
        backRow.AddView(backPill);
        hdrCard.AddView(backRow);

        LinearLayout hero = HRow();
        RoundedArtworkView cover = new(this) { PreferredSizeDp = 88, CornerDp = 14f };
        BindThumbAsync(cover, albumQueue.Count > 0 ? albumQueue[0] : null, alb.AlbumTitle);
        hero.AddView(cover, new LinearLayout.LayoutParams(Dp(88), Dp(88)) { RightMargin = Dp(12) });

        LinearLayout meta = new(this) { Orientation = Orientation.Vertical };
        TextView tTv = Label(alb.AlbumTitle, 16f, ColText);
        tTv.SetTypeface(sfBold, TypefaceStyle.Bold);
        TextView aTv = Label(alb.Artist, 13f, OneUiTheme.PeachText);
        meta.AddView(tTv);
        meta.AddView(aTv);

        LinearLayout albActions = HRow();
        albActions.SetPadding(0, Dp(8), 0, 0);
        LinearLayout playPill = BuildGroupActionPill(OneUiIconType.Play, S["Play"], () => {
            if (albumQueue.Count > 0) {
                OpenLibraryTrack(albumQueue[0], albumQueue, autoPlay: true);
            }
        }, primary: true);
        LinearLayout shufPill = BuildGroupActionPill(null, S["LibShuffleBtn"], () => PlayLibraryQueueShuffled(albumQueue));
        albActions.AddView(playPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        albActions.AddView(shufPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        meta.AddView(albActions);

        hero.AddView(meta, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        hdrCard.AddView(hero);
        libraryListHost.AddView(hdrCard);

        if (merged.Count == 0) {
            RenderLibraryEmptyCard(true);
            return;
        }

        bool multiDisc = merged.Select(m => m.Primary.DiscNumber).Where(d => d > 0).Distinct().Count() > 1;
        int lastDisc = -1;
        int showCount = Math.Min(merged.Count, Math.Min(libraryRenderLimit, albumQueue.Count));
        void AppendAlbumTrackRow(int i) {
            LibraryMergedTrack m = merged[i];
            if (multiDisc && m.Primary.DiscNumber > 0 && m.Primary.DiscNumber != lastDisc) {
                lastDisc = m.Primary.DiscNumber;
                TextView discTv = Label(string.Format(S["LibDiscHeader"], lastDisc), 11.5f, OneUiTheme.PeachText);
                discTv.SetTypeface(sfBold, TypefaceStyle.Bold);
                discTv.SetPadding(Dp(6), Dp(8), Dp(6), Dp(4));
                libraryListHost.AddView(discTv);
            }
            LibraryTrackItem chosen = albumQueue[i];
            libraryListHost.AddView(
                BuildLibraryTrackRow(
                    chosen,
                    albumQueue,
                    i,
                    showThumb: false,
                    badgeOverride: m.Variants.Count > 1 ? m.CombinedBadgeLabel : null,
                    allVariantsForHighlight: m.Variants),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }

        const int InitialBatchItems = 6;
        const int IncrementalBatchItems = 6;
        int firstEnd = Math.Min(showCount, InitialBatchItems);
        for (int i = 0; i < firstEnd; i++) {
            AppendAlbumTrackRow(i);
        }
        ScheduleLibraryIncrementalRender(myRenderGen, firstEnd, showCount, 1, IncrementalBatchItems, AppendAlbumTrackRow,
            () => AddLoadMoreIfNeeded(merged.Count, showCount));
    }

    void RenderGroupTracksDrilldown(
        string title,
        string sub,
        List<LibraryMergedTrack> mergedTracks,
        Action onBack,
        Action onHideFolder = null,
        int myRenderGen = 0) {
        libraryStatusText.Text = $"{title} • {sub}";
        List<LibraryTrackItem> queue = new(mergedTracks.Count);
        for (int i = 0; i < mergedTracks.Count; i++) {
            LibraryTrackItem pref = PickPreferredVariant(mergedTracks[i]);
            if (pref != null) queue.Add(pref);
        }

        LinearLayout hdrCard = Card();
        LinearLayout topRow = HRow();
        topRow.SetPadding(0, 0, 0, Dp(8));
        topRow.AddView(OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.ChevronLeft, S["PlayerLibrary"], onBack));
        hdrCard.AddView(topRow);

        TextView titleTv = Label(title, 15.5f, ColText);
        titleTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;
        hdrCard.AddView(titleTv);

        TextView subTv = Label(sub, 11.5f, OneUiTheme.TextMuted);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.Middle;
        subTv.SetPadding(0, Dp(2), 0, Dp(8));
        hdrCard.AddView(subTv);

        HorizontalScrollView actScroll = new(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never
        };
        LinearLayout actRow = HRow();
        actRow.AddView(
            BuildGroupActionPill(OneUiIconType.Play, S["Play"], () => {
                if (queue.Count > 0) OpenLibraryTrack(queue[0], queue, autoPlay: true);
            }, primary: true),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        actRow.AddView(
            BuildGroupActionPill(null, S["LibShuffleBtn"], () => PlayLibraryQueueShuffled(queue)),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                RightMargin = onHideFolder != null ? Dp(6) : 0
            });
        if (onHideFolder != null) {
            actRow.AddView(
                BuildGroupActionPill(OneUiIconType.CleanFilter, S["LibHideFolderBtn"], onHideFolder, subtleDanger: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }
        actScroll.AddView(actRow);
        hdrCard.AddView(actScroll);
        libraryListHost.AddView(hdrCard);

        if (mergedTracks.Count == 0) {
            RenderLibraryEmptyCard(true);
            return;
        }

        int showCount = Math.Min(mergedTracks.Count, Math.Min(libraryRenderLimit, queue.Count));
        void AppendDrilldownRow(int i) {
            LibraryMergedTrack m = mergedTracks[i];
            LibraryTrackItem chosen = queue[i];
            libraryListHost.AddView(
                BuildLibraryTrackRow(
                    chosen,
                    queue,
                    i,
                    showThumb: true,
                    badgeOverride: m.Variants.Count > 1 ? m.CombinedBadgeLabel : null,
                    allVariantsForHighlight: m.Variants),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }

        const int InitialBatchItems = 6;
        const int IncrementalBatchItems = 6;
        int firstEnd = Math.Min(showCount, InitialBatchItems);
        for (int i = 0; i < firstEnd; i++) {
            AppendDrilldownRow(i);
        }
        ScheduleLibraryIncrementalRender(myRenderGen, firstEnd, showCount, 1, IncrementalBatchItems, AppendDrilldownRow,
            () => AddLoadMoreIfNeeded(mergedTracks.Count, showCount));
    }

    LinearLayout BuildGroupRow(
        string title,
        string sub,
        LibraryTrackItem sample,
        List<LibraryTrackItem> queue,
        Action onOpen,
        bool isFolder = false,
        Action onHideFolder = null) {
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.Background = GetCachedGroupCardDrawable();
        card.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        OneUiTheme.AttachRecoil(card, 0.97f);

        LinearLayout topRow = HRow();
        if (isFolder) {
            topRow.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Folder, OneUiTheme.IconStudioExport, 40),
                new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(12) });
        } else {
            RoundedArtworkView thumb = new(this) { PreferredSizeDp = 42, CornerDp = 10f };
            BindThumbAsync(thumb, sample, title);
            topRow.AddView(thumb, new LinearLayout.LayoutParams(Dp(42), Dp(42)) { RightMargin = Dp(12) });
        }

        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView tv = Label(title, 13.5f, ColText);
        tv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        tv.SetSingleLine(true);
        tv.Ellipsize = TextUtils.TruncateAt.End;
        TextView stv = Label(sub, 11f, OneUiTheme.TextMuted);
        stv.SetSingleLine(true);
        stv.Ellipsize = TextUtils.TruncateAt.End;
        col.AddView(tv);
        col.AddView(stv);
        topRow.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(topRow);

        LinearLayout actRow = HRow();
        actRow.AddView(
            BuildGroupActionPill(OneUiIconType.Play, S["Play"], () => {
                if (queue != null && queue.Count > 0) OpenLibraryTrack(queue[0], queue, autoPlay: true);
            }, primary: true),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        actRow.AddView(
            BuildGroupActionPill(null, S["LibShuffleBtn"], () => PlayLibraryQueueShuffled(queue)),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                RightMargin = isFolder && onHideFolder != null ? Dp(6) : 0
            });
        if (isFolder && onHideFolder != null) {
            actRow.AddView(
                BuildGroupActionPill(OneUiIconType.CleanFilter, S["LibHideFolderBtn"], onHideFolder, subtleDanger: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }
        card.AddView(actRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });

        topRow.Click += (_, _) => onOpen?.Invoke();
        card.Click += (_, _) => onOpen?.Invoke();
        return card;
    }

    LinearLayout BuildLibraryTrackRow(
        LibraryTrackItem item,
        IReadOnlyList<LibraryTrackItem> queue,
        int indexInQueue,
        bool showThumb = true,
        string badgeOverride = null,
        IReadOnlyList<LibraryTrackItem> allVariantsForHighlight = null) {
        string loadedPath = LoadedFile?.Path;
        bool isPlayingThis = false;
        if (loadedPath != null) {
            if (string.Equals(loadedPath, item.FilePath, StringComparison.OrdinalIgnoreCase)) {
                isPlayingThis = true;
            } else if (allVariantsForHighlight != null) {
                for (int vIdx = 0; vIdx < allVariantsForHighlight.Count; vIdx++) {
                    if (string.Equals(loadedPath, allVariantsForHighlight[vIdx].FilePath, StringComparison.OrdinalIgnoreCase)) {
                        isPlayingThis = true;
                        break;
                    }
                }
            }
        }

        LinearLayout row = HRow();
        row.Background = GetCachedTrackRowDrawable(isPlayingThis);
        row.SetPadding(Dp(10), Dp(9), Dp(10), Dp(9));
        OneUiTheme.AttachRecoil(row, 0.97f);

        if (showThumb) {
            RoundedArtworkView thumb = new(this) { PreferredSizeDp = 44, CornerDp = 10f };
            BindThumbAsync(thumb, item, item.Title);
            row.AddView(thumb, new LinearLayout.LayoutParams(Dp(44), Dp(44)) { RightMargin = Dp(10) });
        } else {
            string numStr = item.TrackNumber > 0 ? $"{item.TrackNumber}" : $"{indexInQueue + 1}";
            TextView numTv = Label(numStr, 12.5f, isPlayingThis ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
            numTv.Gravity = GravityFlags.Center;
            numTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
            row.AddView(numTv, new LinearLayout.LayoutParams(Dp(26), ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
        }

        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = Label(item.Title, 13.5f, isPlayingThis ? OneUiTheme.PrimaryHigh : ColText);
        titleTv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;

        bool hasArtist = !string.IsNullOrWhiteSpace(item.Artist);
        bool hasAlbum = !string.IsNullOrWhiteSpace(item.Album);
        string subLine = (hasArtist && hasAlbum)
            ? $"{item.Artist} — {item.Album}"
            : (hasArtist ? item.Artist : (hasAlbum ? item.Album : item.FolderName));
        TextView subTv = Label(subLine, 11f, OneUiTheme.TextMuted);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.End;
        col.AddView(titleTv);
        col.AddView(subTv);
        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        // Spatial / Container format badge (or merged ATMOS + FLAC badge) & duration column
        LinearLayout rightCol = new(this) { Orientation = Orientation.Vertical };
        rightCol.SetGravity(GravityFlags.End);
        string badgeLabel = !string.IsNullOrEmpty(badgeOverride)
            ? badgeOverride
            : (!string.IsNullOrEmpty(item.AudioBadge) && item.AudioBadge != item.FormatBadge
                ? $"{item.FormatBadge} • {item.AudioBadge}"
                : item.FormatBadge);
        if (string.IsNullOrWhiteSpace(badgeLabel)) {
            badgeLabel = item.IsVideo ? S["LibVideoBadge"] : S["AudioBadgeDefault"];
        }
        bool anySpatial = item.IsSpatial;
        bool anySurround = item.IsSurround;
        if ((!anySpatial || !anySurround) && allVariantsForHighlight != null) {
            for (int vIdx = 0; vIdx < allVariantsForHighlight.Count; vIdx++) {
                LibraryTrackItem v = allVariantsForHighlight[vIdx];
                if (v.IsSpatial) anySpatial = true;
                if (v.IsSurround) anySurround = true;
                if (anySpatial && anySurround) break;
            }
        }
        Color badgeTextCol = anySpatial
            ? OneUiTheme.PrimaryHigh
            : (anySurround ? OneUiTheme.PeachText : OneUiTheme.TextMuted);
        TextView fmtTv = Label(badgeLabel, 9.5f, badgeTextCol);
        fmtTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        fmtTv.SetSingleLine(true);
        fmtTv.Ellipsize = TextUtils.TruncateAt.End;
        fmtTv.SetMaxWidth(Dp(126));
        fmtTv.SetPadding(Dp(6), Dp(2), Dp(6), Dp(2));
        fmtTv.Background = GetCachedBadgeDrawable(anySpatial);
        rightCol.AddView(fmtTv);

        if (item.DurationMs > 0) {
            TimeSpan ts = TimeSpan.FromMilliseconds(item.DurationMs);
            TextView durTv = Label(ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss"), 10f, OneUiTheme.TextMuted);
            durTv.SetPadding(0, Dp(2), 0, 0);
            rightCol.AddView(durTv);
        }
        row.AddView(rightCol);

        row.Click += (_, _) => OpenLibraryTrack(item, queue, autoPlay: true);
        return row;
    }

    async void BindThumbAsync(RoundedArtworkView target, LibraryTrackItem item, string seedTitle) {
        if (target == null) return;
        Bitmap cached = item != null ? MediaLibraryService.TryGetCachedThumbnail(item.CacheKey) : null;
        if (IsBitmapUsable(cached)) {
            target.SetArtwork(cached, OneUiTheme.Primary);
            return;
        }
        target.SetArtwork(defaultFallbackCover ??= MediaMetadataHelper.CreateSpatialFallbackArtwork("Cavernize", OneUiTheme.PrimaryHigh, OneUiTheme.Secondary, 256), OneUiTheme.Primary);
        if (item == null) return;

        string expectedKey = item.CacheKey;
        target.Tag = expectedKey;
        try {
            Bitmap loaded = await MediaLibraryService.LoadThumbnailAsync(this, item, 220).ConfigureAwait(false);
            if (!IsBitmapUsable(loaded) || uiDestroyed) return;
            RunOnUiThread(() => {
                if (uiDestroyed || !IsBitmapUsable(loaded)) return;
                if (string.Equals(target.Tag?.ToString(), expectedKey, StringComparison.Ordinal)) {
                    target.SetArtwork(loaded, OneUiTheme.Primary);
                }
            });
        } catch {
            // Keep fallback cover safely
        }
    }

    void OpenLibraryTrack(LibraryTrackItem item, IReadOnlyList<LibraryTrackItem> queue, bool autoPlay, bool preserveCategoryPreference = false) {
        if (item == null) return;
        if (!preserveCategoryPreference) {
            preferredAudioCategory = AudioQualityAnalyzer.ClassifyCategory(item);
        }
        activeLibraryQueue.Clear();
        if (queue != null) {
            activeLibraryQueue.AddRange(queue);
            activeLibraryQueueIndex = activeLibraryQueue.IndexOf(item);
        } else {
            activeLibraryQueue.Add(item);
            activeLibraryQueueIndex = 0;
        }
        currentMediaUri = item.ContentUri;
        SetMusicPlayerSection(openLibrary: false);
        OpenLocalPathWithFallbackAsync(item.FilePath, switchToTab: 0, autoPlay: autoPlay);
    }

}
