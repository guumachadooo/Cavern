using System.Collections.Concurrent;

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;

using IOPath = System.IO.Path;

namespace CavernizeAndroid.Views;

public enum ExplorerPickMode {
    SingleFile,
    MultiFile,
    MultiFolder
}

public sealed class FileExplorerOptions {
    public string Title { get; init; } = "";
    public ExplorerPickMode Mode { get; init; } = ExplorerPickMode.SingleFile;
    public HashSet<string> AllowedExtensions { get; init; }
    public bool AllowAllFiles { get; init; }
    public string InitialDirectory { get; init; }
    public IEnumerable<string> InitiallySelectedPaths { get; init; }
    public string ConfirmLabel { get; init; }
    public Action OnFallbackSystemPicker { get; init; }
    public Action<IReadOnlyList<string>> OnConfirm { get; init; }
}

/// <summary>
/// Intelligent, One UI 9 frosted in-app File &amp; Folder Explorer:
/// - Prunes dead-end folders that contain zero supported audio/video files anywhere in their subtree
/// - Shows recursive/direct file counts &amp; spatial Atmos/TrueHD badges per folder and file
/// - Supports Single File, Multi-File Batch Selection, and Multi-Folder Include/Exclude Selection
/// </summary>
public static class OneUiFileExplorerDialog {
    sealed class DirScanEntry {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public int DirectMatchCount { get; init; }
        public int TotalSampledCount { get; init; }
        public bool HasSpatial { get; init; }
    }

    sealed class FileScanEntry {
        public string Path { get; init; } = "";
        public Android.Net.Uri ContentUri { get; init; }
        public string Name { get; init; } = "";
        public string Badge { get; init; } = "";
        public bool IsSpatial { get; init; }
        public long SizeBytes { get; init; }
    }

    public static void Show(Context ctx, FileExplorerOptions opts) {
        if (ctx == null || opts == null) return;
        AndroidStrings S = AndroidStrings.Active;

        bool IsUiDead() => ctx is Activity act && (act.IsFinishing || act.IsDestroyed);

        string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/') ?? "/storage/emulated/0";
        string requestedDir = opts.InitialDirectory;
        if (!string.IsNullOrWhiteSpace(requestedDir) &&
            requestedDir.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) {
            try {
                requestedDir = AndroidFileHelper.TryGetOriginalDirectory(ctx, Android.Net.Uri.Parse(requestedDir), null);
            } catch {
                requestedDir = null;
            }
        }
        string appWorkingDir = AndroidFileHelper.GetWorkingOutputDir(ctx);
        string startDir = !string.IsNullOrWhiteSpace(requestedDir) && Directory.Exists(requestedDir)
            ? requestedDir.TrimEnd('/')
            : (Directory.Exists(extRoot) ? extRoot : appWorkingDir.TrimEnd('/'));

        int dp(float v) => OneUiTheme.Dp(ctx, v);
        Typeface sfBold = OneUiTheme.GetAppleFontBold(ctx);
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(ctx);
        Typeface sfReg = OneUiTheme.GetAppleFontRegular(ctx);

        HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
        if (opts.InitiallySelectedPaths != null) {
            foreach (string p in opts.InitiallySelectedPaths) {
                if (!string.IsNullOrWhiteSpace(p)) selectedPaths.Add(p.TrimEnd('/'));
            }
        }

        ConcurrentDictionary<string, (bool hasAny, int directCount, int sampledTotal, bool hasSpatial)> treeMemo =
            new(StringComparer.OrdinalIgnoreCase);

        bool FileMatches(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            string name = IOPath.GetFileName(path);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) return false;
            if (opts.AllowAllFiles) return true;
            string ext = IOPath.GetExtension(path);
            if (opts.AllowedExtensions != null && opts.AllowedExtensions.Count > 0) {
                return !string.IsNullOrEmpty(ext) && opts.AllowedExtensions.Contains(ext);
            }
            return MediaLibraryService.IsSupportedMediaExtension(ext);
        }

        (bool hasAny, int directCount, int sampledTotal, bool hasSpatial) InspectDirTree(string dir, int depth) {
            if (string.IsNullOrEmpty(dir)) return (false, 0, 0, false);
            string norm = dir.TrimEnd('/');
            if (treeMemo.TryGetValue(norm, out var cached)) return cached;
            if (depth > 6) return (false, 0, 0, false);

            string folderLeaf = IOPath.GetFileName(norm) ?? "";
            if (folderLeaf.StartsWith('.') ||
                norm.Contains("/Android/", StringComparison.OrdinalIgnoreCase) ||
                norm.EndsWith("/Android", StringComparison.OrdinalIgnoreCase) ||
                norm.Contains("/Cavernize/Logs", StringComparison.OrdinalIgnoreCase)) {
                var skip = (false, 0, 0, false);
                treeMemo[norm] = skip;
                return skip;
            }

            int direct = 0;
            int total = 0;
            bool spatial = false;
            try {
                foreach (string f in Directory.EnumerateFiles(norm)) {
                    if (!FileMatches(f)) continue;
                    direct++;
                    total++;
                    string lower = IOPath.GetFileName(f).ToLowerInvariant();
                    string ext = IOPath.GetExtension(f).ToLowerInvariant();
                    if (ext is ".atmos" or ".thd" or ".mlp" or ".eac3" or ".ec3" ||
                        lower.Contains("atmos") || lower.Contains("truehd") || lower.Contains("eac3") || lower.Contains("joc")) {
                        spatial = true;
                    }
                    if (opts.Mode != ExplorerPickMode.MultiFolder && total >= 64) break;
                }

                if (total == 0 || opts.Mode == ExplorerPickMode.MultiFolder) {
                    foreach (string sub in Directory.EnumerateDirectories(norm)) {
                        var subRes = InspectDirTree(sub, depth + 1);
                        if (subRes.hasAny) {
                            total += Math.Max(1, subRes.sampledTotal);
                            if (subRes.hasSpatial) spatial = true;
                            if (total > 0 && depth > 0 && opts.Mode != ExplorerPickMode.MultiFolder) {
                                // Early exit for subfolder pruning once we know it isn't a dead-end!
                                break;
                            }
                        }
                    }
                }
            } catch {
                // Restricted directory
            }

            var res = (total > 0, direct, total, spatial);
            treeMemo[norm] = res;
            return res;
        }

        // Dialog Layout
        LinearLayout root = new(ctx) { Orientation = Orientation.Vertical };

        string currentDir = startDir;
        List<DirScanEntry> loadedDirs = [];
        List<FileScanEntry> loadedFiles = [];
        int navGen = 0;
        Dialog dlg = null;

        // Optional All Files Access prompt if not yet granted
        if (!AndroidFileHelper.HasAllFilesAccess() && OperatingSystem.IsAndroidVersionAtLeast(30)) {
            LinearLayout permBar = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
            permBar.SetGravity(GravityFlags.CenterVertical);
            permBar.SetPadding(dp(12), dp(8), dp(12), dp(8));
            permBar.Background = OneUiTheme.CreateRoundedDrawable(ctx, OneUiTheme.WithAlpha(OneUiTheme.Primary, 55), 14f, OneUiTheme.Primary, 1f);
            OneUiTheme.AttachRecoil(permBar, 0.97f);

            TextView pTv = new(ctx) { Text = S["ExplorerGrantStorage"], TextSize = 11.5f };
            pTv.SetTextColor(OneUiTheme.TextPrimary);
            pTv.Typeface = sfSemi;
            permBar.AddView(pTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            permBar.Click += (_, _) => {
                if (!OperatingSystem.IsAndroidVersionAtLeast(30)) {
                    return;
                }
                try {
                    Intent permIntent = new(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                        Android.Net.Uri.Parse($"package:{ctx.PackageName}"));
                    ctx.StartActivity(permIntent);
                } catch {
                    try {
                        Intent appSettings = new(Android.Provider.Settings.ActionApplicationDetailsSettings,
                            Android.Net.Uri.Parse($"package:{ctx.PackageName}"));
                        ctx.StartActivity(appSettings);
                    } catch {
                        dlg?.Dismiss();
                        opts.OnFallbackSystemPicker?.Invoke();
                    }
                }
            };
            root.AddView(permBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = dp(8) });
        }

        // Quick Shortcut Roots
        HorizontalScrollView shortcutsScroll = new(ctx) { HorizontalScrollBarEnabled = false };
        LinearLayout shortcutsRow = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        shortcutsRow.SetGravity(GravityFlags.CenterVertical);
        shortcutsScroll.AddView(shortcutsRow);
        root.AddView(shortcutsScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = dp(8) });

        // Breadcrumb bar
        HorizontalScrollView crumbScroll = new(ctx) { HorizontalScrollBarEnabled = false };
        LinearLayout crumbRow = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        crumbRow.SetGravity(GravityFlags.CenterVertical);
        crumbScroll.AddView(crumbRow);
        root.AddView(crumbScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = dp(8) });

        // Search + Action Bar
        LinearLayout searchRow = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        searchRow.SetGravity(GravityFlags.CenterVertical);

        LinearLayout searchPill = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        searchPill.SetGravity(GravityFlags.CenterVertical);
        searchPill.SetPadding(dp(11), dp(6), dp(10), dp(6));
        searchPill.Background = OneUiTheme.CreateRoundedDrawable(ctx, OneUiTheme.SurfaceModalRow, 999f);
        OneUiIconView srchIco = new(ctx, OneUiIconType.Search, OneUiTheme.TextMuted);
        EditText filterInput = new(ctx) {
            Hint = S["ExplorerFilterHint"],
            TextSize = 12.5f
        };
        filterInput.Typeface = sfReg;
        filterInput.SetTextColor(OneUiTheme.TextPrimary);
        filterInput.SetHintTextColor(OneUiTheme.TextMuted);
        filterInput.SetBackgroundColor(Color.Transparent);
        filterInput.SetSingleLine(true);
        filterInput.SetPadding(0, dp(2), 0, dp(2));
        searchPill.AddView(srchIco, new LinearLayout.LayoutParams(dp(15), dp(15)) { RightMargin = dp(7) });
        searchPill.AddView(filterInput, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        searchRow.AddView(searchPill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        TextView selectAllFolderBtn = null;
        if (opts.Mode == ExplorerPickMode.MultiFile) {
            selectAllFolderBtn = new TextView(ctx) { Text = S["SelectAll"], TextSize = 11.5f };
            selectAllFolderBtn.Typeface = sfBold;
            selectAllFolderBtn.SetTextColor(OneUiTheme.PrimaryHigh);
            selectAllFolderBtn.SetSingleLine(true);
            selectAllFolderBtn.SetPadding(dp(11), dp(7), dp(11), dp(7));
            selectAllFolderBtn.Background = OneUiTheme.CreateRoundedDrawable(ctx, OneUiTheme.WithAlpha(OneUiTheme.Primary, 45), 999f);
            OneUiTheme.AttachRecoil(selectAllFolderBtn, 0.93f);
            searchRow.AddView(selectAllFolderBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = dp(6) });
        }
        root.AddView(searchRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = dp(8) });

        TextView statusTv = new(ctx) { Text = S["ExplorerScanning"], TextSize = 11.5f };
        statusTv.SetTextColor(OneUiTheme.TextMuted);
        statusTv.SetPadding(dp(2), 0, dp(2), dp(6));
        root.AddView(statusTv);

        LinearLayout listHost = new(ctx) { Orientation = Orientation.Vertical };
        root.AddView(listHost, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        if (opts.OnFallbackSystemPicker != null) {
            LinearLayout safRow = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
            safRow.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);
            safRow.SetPadding(0, dp(8), 0, 0);
            TextView safBtn = new(ctx) { Text = S["ExplorerSystemFallback"], TextSize = 11.5f };
            safBtn.Typeface = sfSemi;
            safBtn.SetTextColor(OneUiTheme.PrimaryHigh);
            safBtn.SetPadding(dp(10), dp(6), dp(10), dp(6));
            safBtn.Background = OneUiTheme.CreateRoundedDrawable(ctx, OneUiTheme.SurfaceModalRow, 999f);
            OneUiTheme.AttachRecoil(safBtn, 0.94f);
            safRow.AddView(safBtn);
            root.AddView(safRow);
            safBtn.Tag = "saf_btn";
        }

        Button confirmActionBtn = null;

        void ConfirmResolvedPaths(IReadOnlyList<string> rawPaths) {
            if (rawPaths == null || rawPaths.Count == 0) {
                opts.OnConfirm?.Invoke(rawPaths ?? []);
                return;
            }
            bool hasContentUri = rawPaths.Any(p => !string.IsNullOrEmpty(p) && p.StartsWith("content://", StringComparison.OrdinalIgnoreCase));
            if (!hasContentUri) {
                opts.OnConfirm?.Invoke(rawPaths);
                return;
            }
            Task.Run(() => {
                List<string> resolved = [];
                foreach (string raw in rawPaths) {
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (raw.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) {
                        try {
                            string local = AndroidFileHelper.ResolveReadablePath(ctx, Android.Net.Uri.Parse(raw));
                            if (!string.IsNullOrEmpty(local)) resolved.Add(local);
                        } catch {
                            // Skip unreadable content URI
                        }
                    } else {
                        resolved.Add(raw);
                    }
                }
                new Handler(Looper.MainLooper!).Post(() => {
                    if (IsUiDead()) return;
                    opts.OnConfirm?.Invoke(resolved);
                });
            });
        }

        void RefreshSelectionFooter() {
            if (opts.Mode == ExplorerPickMode.SingleFile || confirmActionBtn == null) return;
            string baseLbl = !string.IsNullOrWhiteSpace(opts.ConfirmLabel)
                ? opts.ConfirmLabel
                : (opts.Mode == ExplorerPickMode.MultiFolder ? S["ExplorerApplyFolders"] : S["ExplorerAddFiles"]);
            confirmActionBtn.Text = selectedPaths.Count > 0 ? $"{baseLbl} ({selectedPaths.Count})" : baseLbl;
        }

        void RenderCurrentItems() {
            listHost.RemoveAllViews();
            string q = (filterInput.Text ?? "").Trim();
            bool hasQ = !string.IsNullOrEmpty(q);

            List<DirScanEntry> dirs = hasQ
                ? loadedDirs.Where(d => d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList()
                : loadedDirs;
            List<FileScanEntry> files = hasQ
                ? loadedFiles.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList()
                : loadedFiles;

            if (dirs.Count == 0 && files.Count == 0) {
                TextView emptyTv = new(ctx) { Text = S["ExplorerEmpty"], TextSize = 12.5f };
                emptyTv.SetTextColor(OneUiTheme.TextMuted);
                emptyTv.SetPadding(dp(6), dp(14), dp(6), dp(10));
                listHost.AddView(emptyTv);

                if (!AndroidFileHelper.HasAllFilesAccess() && opts.OnFallbackSystemPicker != null) {
                    TextView safInlineBtn = new(ctx) { Text = S["ExplorerSystemFallback"], TextSize = 12f };
                    safInlineBtn.Typeface = sfBold;
                    safInlineBtn.SetTextColor(OneUiTheme.PrimaryHigh);
                    safInlineBtn.SetPadding(dp(14), dp(8), dp(14), dp(8));
                    safInlineBtn.Background = OneUiTheme.CreateRoundedDrawable(ctx, OneUiTheme.WithAlpha(OneUiTheme.Primary, 45), 999f, OneUiTheme.Primary, 1f);
                    OneUiTheme.AttachRecoil(safInlineBtn, 0.94f);
                    safInlineBtn.Click += (_, _) => {
                        dlg?.Dismiss();
                        opts.OnFallbackSystemPicker?.Invoke();
                    };
                    listHost.AddView(safInlineBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                        LeftMargin = dp(6),
                        BottomMargin = dp(8)
                    });
                }
                return;
            }

            foreach (DirScanEntry d in dirs) {
                bool isChecked = selectedPaths.Contains(d.Path);
                LinearLayout row = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(dp(10), dp(9), dp(10), dp(9));
                row.Background = OneUiTheme.CreateRoundedDrawable(
                    ctx,
                    isChecked ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 68) : OneUiTheme.SurfaceModalRow,
                    16f,
                    isChecked ? OneUiTheme.PrimaryHigh : (d.HasSpatial ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 80) : null),
                    isChecked || d.HasSpatial ? 1f : 0f);
                OneUiTheme.AttachRecoil(row, 0.98f);

                if (opts.Mode == ExplorerPickMode.MultiFolder) {
                    OneUiSwitch cb = new(ctx) { Checked = isChecked };
                                        cb.CheckedChange += (_, e) => {
                        if (e.IsChecked) selectedPaths.Add(d.Path);
                        else selectedPaths.Remove(d.Path);
                        row.Background = OneUiTheme.CreateRoundedDrawable(
                            ctx,
                            e.IsChecked ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 68) : OneUiTheme.SurfaceModalRow,
                            16f,
                            e.IsChecked ? OneUiTheme.PrimaryHigh : null,
                            e.IsChecked ? 1f : 0f);
                        RefreshSelectionFooter();
                    };
                    row.AddView(cb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = dp(4) });
                }

                row.AddView(OneUiTheme.CreateVectorIconBadge(ctx, OneUiIconType.Folder,
                    d.HasSpatial ? OneUiTheme.Primary : OneUiTheme.IconStudioExport, 34),
                    new LinearLayout.LayoutParams(dp(34), dp(34)) { RightMargin = dp(10) });

                LinearLayout col = new(ctx) { Orientation = Orientation.Vertical };
                TextView nameTv = new(ctx) { Text = d.Name, TextSize = 13f };
                nameTv.Typeface = sfBold;
                nameTv.SetTextColor(OneUiTheme.TextPrimary);
                nameTv.SetSingleLine(true);
                nameTv.Ellipsize = TextUtils.TruncateAt.End;

                string sub = d.DirectMatchCount > 0
                    ? string.Format(S["ExplorerDirectCount"], d.DirectMatchCount)
                    : string.Format(S["ExplorerNestedCount"], d.TotalSampledCount);
                if (d.HasSpatial) sub += " • Atmos/3D";
                TextView subTv = new(ctx) { Text = sub, TextSize = 11f };
                subTv.Typeface = sfReg;
                subTv.SetTextColor(d.HasSpatial ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
                subTv.SetSingleLine(true);

                col.AddView(nameTv);
                col.AddView(subTv);
                row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                OneUiIconView chev = new(ctx, OneUiIconType.ChevronLeft, OneUiTheme.TextMuted) { Rotation = 180f };
                row.AddView(chev, new LinearLayout.LayoutParams(dp(14), dp(14)) { LeftMargin = dp(6) });

                row.Click += (_, _) => NavigateTo(d.Path);
                listHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = dp(6) });
            }

            if (opts.Mode == ExplorerPickMode.MultiFolder && files.Count > 0) {
                TextView previewHdr = new(ctx) {
                    Text = string.Format(S["ExplorerFilesHere"], files.Count),
                    TextSize = 11f
                };
                previewHdr.SetTextColor(OneUiTheme.TextMuted);
                previewHdr.SetPadding(dp(4), dp(4), dp(4), dp(4));
                listHost.AddView(previewHdr);
            }

            int maxFiles = opts.Mode == ExplorerPickMode.MultiFolder ? Math.Min(files.Count, 8) : Math.Min(files.Count, 120);
            for (int i = 0; i < maxFiles; i++) {
                FileScanEntry f = files[i];
                bool isSel = selectedPaths.Contains(f.Path);
                LinearLayout row = new(ctx) { Orientation = Orientation.Horizontal, BaselineAligned = false };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(dp(10), dp(8), dp(10), dp(8));
                row.Background = OneUiTheme.CreateRoundedDrawable(
                    ctx,
                    isSel ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 64) : OneUiTheme.SurfaceModalRow,
                    14f,
                    isSel ? OneUiTheme.PrimaryHigh : null,
                    isSel ? 1f : 0f);
                if (opts.Mode != ExplorerPickMode.MultiFolder) {
                    OneUiTheme.AttachRecoil(row, 0.98f);
                }

                OneUiSwitch fileCb = null;
                if (opts.Mode == ExplorerPickMode.MultiFile) {
                    fileCb = new OneUiSwitch(ctx) { Checked = isSel };
                                        row.AddView(fileCb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = dp(4) });
                } else {
                    OneUiIconView noteIco = new(ctx, OneUiIconType.MusicFile,
                        f.IsSpatial ? OneUiTheme.PrimaryHigh : OneUiTheme.TextSub);
                    row.AddView(noteIco, new LinearLayout.LayoutParams(dp(18), dp(18)) { RightMargin = dp(10) });
                }

                LinearLayout col = new(ctx) { Orientation = Orientation.Vertical };
                TextView nameTv = new(ctx) { Text = f.Name, TextSize = 12.5f };
                nameTv.Typeface = opts.Mode == ExplorerPickMode.MultiFolder ? sfReg : sfSemi;
                nameTv.SetTextColor(opts.Mode == ExplorerPickMode.MultiFolder ? OneUiTheme.TextSub : OneUiTheme.TextPrimary);
                nameTv.SetSingleLine(true);
                nameTv.Ellipsize = TextUtils.TruncateAt.Middle;

                string sizeStr = f.SizeBytes >= 1024 * 1024
                    ? $"{f.SizeBytes / (1024.0 * 1024.0):0.0} MB"
                    : $"{Math.Max(1, f.SizeBytes / 1024)} KB";
                TextView subTv = new(ctx) { Text = $"{f.Badge} • {sizeStr}", TextSize = 10.5f };
                subTv.SetTextColor(f.IsSpatial ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);

                col.AddView(nameTv);
                col.AddView(subTv);
                row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                if (opts.Mode == ExplorerPickMode.SingleFile) {
                    row.Click += (_, _) => {
                        dlg?.Dismiss();
                        ConfirmResolvedPaths([f.Path]);
                    };
                } else if (opts.Mode == ExplorerPickMode.MultiFile) {
                    void ToggleFile(bool next) {
                        if (next) selectedPaths.Add(f.Path);
                        else selectedPaths.Remove(f.Path);
                        row.Background = OneUiTheme.CreateRoundedDrawable(
                            ctx,
                            next ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 64) : OneUiTheme.SurfaceModalRow,
                            14f, next ? OneUiTheme.PrimaryHigh : null, next ? 1f : 0f);
                        RefreshSelectionFooter();
                    }
                    fileCb.CheckedChange += (_, e) => ToggleFile(e.IsChecked);
                    row.Click += (_, _) => {
                        bool next = !fileCb.Checked;
                        fileCb.Checked = next;
                    };
                }

                listHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = dp(5) });
            }
        }

        void RebuildBreadcrumbs() {
            crumbRow.RemoveAllViews();
            string rel = currentDir.StartsWith(extRoot, StringComparison.OrdinalIgnoreCase)
                ? currentDir[extRoot.Length..].Trim('/')
                : currentDir.Trim('/');

            void AddCrumb(string label, string targetPath, bool isLast) {
                TextView tv = new(ctx) { Text = label, TextSize = 11f };
                tv.Typeface = isLast ? sfBold : sfReg;
                tv.SetTextColor(isLast ? OneUiTheme.TextPrimary : OneUiTheme.TextSub);
                tv.SetPadding(dp(9), dp(4), dp(9), dp(4));
                tv.Background = OneUiTheme.CreateRoundedDrawable(
                    ctx,
                    isLast ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 75) : OneUiTheme.SurfaceModalRow,
                    999f);
                OneUiTheme.AttachRecoil(tv, 0.94f);
                tv.Click += (_, _) => NavigateTo(targetPath);
                crumbRow.AddView(tv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = dp(4) });
            }

            string[] segs = string.IsNullOrEmpty(rel) ? [] : rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            AddCrumb(S["ExplorerRoot"], extRoot, segs.Length == 0);
            string acc = extRoot;
            for (int i = 0; i < segs.Length; i++) {
                acc = IOPath.Combine(acc, segs[i]);
                string snap = acc;
                TextView sep = new(ctx) { Text = "›", TextSize = 12f };
                sep.SetTextColor(OneUiTheme.TextMuted);
                sep.SetPadding(dp(2), 0, dp(4), 0);
                crumbRow.AddView(sep);
                AddCrumb(segs[i], snap, i == segs.Length - 1);
            }
        }

        void NavigateTo(string dir) {
            if (string.IsNullOrEmpty(dir)) return;
            currentDir = dir.TrimEnd('/');
            RebuildBreadcrumbs();
            int myGen = ++navGen;
            statusTv.Text = S["ExplorerScanning"];
            listHost.RemoveAllViews();

            Task.Run(() => {
                List<DirScanEntry> dirs = [];
                List<FileScanEntry> files = [];
                try {
                    if (Directory.Exists(currentDir)) {
                        foreach (string sub in Directory.EnumerateDirectories(currentDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                            var info = InspectDirTree(sub, 0);
                            if (!info.hasAny) continue; // Dead-end folder pruning!
                            dirs.Add(new DirScanEntry {
                                Path = sub.TrimEnd('/'),
                                Name = IOPath.GetFileName(sub) ?? sub,
                                DirectMatchCount = info.directCount,
                                TotalSampledCount = info.sampledTotal,
                                HasSpatial = info.hasSpatial
                            });
                        }

                        foreach (string f in Directory.EnumerateFiles(currentDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                            if (!FileMatches(f)) continue;
                            string fn = IOPath.GetFileName(f);
                            string lower = fn.ToLowerInvariant();
                            string ext = IOPath.GetExtension(f).TrimStart('.').ToUpperInvariant();
                            bool sp = ext is "ATMOS" or "THD" or "MLP" or "EAC3" or "EC3" ||
                                lower.Contains("atmos") || lower.Contains("truehd") || lower.Contains("joc");
                            long len = 0;
                            try { len = new FileInfo(f).Length; } catch { }
                            files.Add(new FileScanEntry {
                                Path = f,
                                Name = fn,
                                Badge = string.IsNullOrEmpty(ext) ? "ARQUIVO" : ext,
                                IsSpatial = sp,
                                SizeBytes = len
                            });
                        }
                    }
                } catch {
                    // Protected folder fallback
                }

                // Scoped Storage MediaStore fallback when direct POSIX enumeration finds nothing under API 30-36
                if (dirs.Count == 0 && files.Count == 0 && !AndroidFileHelper.HasAllFilesAccess()) {
                    List<MediaStoreEntry> mediaEntries = AndroidFileHelper.QueryMediaStoreEntries(
                        ctx, opts.AllowedExtensions, opts.AllowAllFiles);
                    string curPrefix = currentDir.TrimEnd('/') + "/";
                    Dictionary<string, (int direct, int total, bool spatial)> childBuckets = new(StringComparer.OrdinalIgnoreCase);

                    foreach (MediaStoreEntry entry in mediaEntries) {
                        string entryFolder = (entry.FolderPath ?? "").TrimEnd('/');
                        if (string.Equals(entryFolder, currentDir, StringComparison.OrdinalIgnoreCase)) {
                            if (opts.Mode == ExplorerPickMode.MultiFolder) {
                                continue;
                            }
                            string lower = entry.DisplayName.ToLowerInvariant();
                            string ext = IOPath.GetExtension(entry.DisplayName).TrimStart('.').ToUpperInvariant();
                            bool sp = ext is "ATMOS" or "THD" or "MLP" or "EAC3" or "EC3" ||
                                lower.Contains("atmos") || lower.Contains("truehd") || lower.Contains("joc");
                            string pickTarget = !string.IsNullOrEmpty(entry.DirectPath)
                                ? entry.DirectPath
                                : (entry.ContentUri?.ToString() ?? "");
                            if (string.IsNullOrEmpty(pickTarget)) continue;
                            files.Add(new FileScanEntry {
                                Path = pickTarget,
                                ContentUri = entry.ContentUri,
                                Name = entry.DisplayName,
                                Badge = string.IsNullOrEmpty(ext) ? "ARQUIVO" : ext,
                                IsSpatial = sp,
                                SizeBytes = entry.SizeBytes
                            });
                        } else if (entryFolder.StartsWith(curPrefix, StringComparison.OrdinalIgnoreCase)) {
                            string rel = entryFolder[curPrefix.Length..];
                            int slashIdx = rel.IndexOf('/');
                            string childName = slashIdx >= 0 ? rel[..slashIdx] : rel;
                            if (string.IsNullOrEmpty(childName)) continue;
                            string childPath = IOPath.Combine(currentDir, childName).TrimEnd('/');
                            bool isDirect = slashIdx < 0;
                            string lower = entry.DisplayName.ToLowerInvariant();
                            string ext = IOPath.GetExtension(entry.DisplayName).TrimStart('.').ToUpperInvariant();
                            bool sp = ext is "ATMOS" or "THD" or "MLP" or "EAC3" or "EC3" ||
                                lower.Contains("atmos") || lower.Contains("truehd") || lower.Contains("joc");
                            childBuckets.TryGetValue(childPath, out var prev);
                            childBuckets[childPath] = (prev.direct + (isDirect ? 1 : 0), prev.total + 1, prev.spatial || sp);
                        }
                    }

                    foreach (var kv in childBuckets.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                        dirs.Add(new DirScanEntry {
                            Path = kv.Key,
                            Name = IOPath.GetFileName(kv.Key) ?? kv.Key,
                            DirectMatchCount = kv.Value.direct,
                            TotalSampledCount = kv.Value.total,
                            HasSpatial = kv.Value.spatial
                        });
                    }
                }

                new Handler(Looper.MainLooper!).Post(() => {
                    if (myGen != navGen || IsUiDead()) return;
                    loadedDirs = dirs;
                    loadedFiles = files;
                    statusTv.Text = $"{dirs.Count} pasta(s) • {files.Count} arquivo(s)";
                    RenderCurrentItems();
                });
            });
        }

        // Populate quick non-empty media shortcuts asynchronously
        Task.Run(() => {
            List<(string label, string path)> candidates = [
                (S["ExplorerRoot"], extRoot),
                ("Music", IOPath.Combine(extRoot, "Music")),
                ("Download", IOPath.Combine(extRoot, "Download")),
                ("Movies", IOPath.Combine(extRoot, "Movies")),
                ("Audiobooks", IOPath.Combine(extRoot, "Audiobooks")),
                ("Cavernize", IOPath.Combine(extRoot, "Download", "Cavernize"))
            ];
            if (!string.IsNullOrEmpty(appWorkingDir) &&
                !string.Equals(appWorkingDir.TrimEnd('/'), IOPath.Combine(extRoot, "Download", "Cavernize"), StringComparison.OrdinalIgnoreCase)) {
                candidates.Add(("Cavernize (App)", appWorkingDir.TrimEnd('/')));
            }

            List<(string label, string path)> valid = [];
            List<MediaStoreEntry> mediaFallback = null;
            foreach (var c in candidates) {
                if (!Directory.Exists(c.path)) continue;
                if (c.path == extRoot || c.path == appWorkingDir.TrimEnd('/') || InspectDirTree(c.path, 0).hasAny) {
                    valid.Add(c);
                } else if (!AndroidFileHelper.HasAllFilesAccess()) {
                    mediaFallback ??= AndroidFileHelper.QueryMediaStoreEntries(ctx, opts.AllowedExtensions, opts.AllowAllFiles, maxResults: 120);
                    string prefix = c.path.TrimEnd('/') + "/";
                    if (mediaFallback.Any(e =>
                        string.Equals(e.FolderPath, c.path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                        e.FolderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) {
                        valid.Add(c);
                    }
                }
            }
            new Handler(Looper.MainLooper!).Post(() => {
                if (IsUiDead()) return;
                shortcutsRow.RemoveAllViews();
                foreach (var (lbl, path) in valid) {
                    TextView chip = new(ctx) { Text = lbl, TextSize = 11.5f };
                    chip.Typeface = sfSemi;
                    chip.SetTextColor(OneUiTheme.TextPrimary);
                    chip.SetPadding(dp(11), dp(5), dp(11), dp(5));
                    chip.Background = OneUiTheme.CreateRoundedDrawable(ctx, OneUiTheme.SurfaceModalRow, 999f);
                    OneUiTheme.AttachRecoil(chip, 0.94f);
                    chip.Click += (_, _) => NavigateTo(path);
                    shortcutsRow.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = dp(6) });
                }
            });
        });

        filterInput.TextChanged += (_, _) => RenderCurrentItems();
        if (selectAllFolderBtn != null) {
            selectAllFolderBtn.Click += (_, _) => {
                bool allSel = loadedFiles.Count > 0 && loadedFiles.All(f => selectedPaths.Contains(f.Path));
                foreach (FileScanEntry f in loadedFiles) {
                    if (allSel) selectedPaths.Remove(f.Path);
                    else selectedPaths.Add(f.Path);
                }
                RenderCurrentItems();
                RefreshSelectionFooter();
            };
        }

        string posLabel = opts.Mode == ExplorerPickMode.SingleFile
            ? S["CancelBtn"]
            : (!string.IsNullOrWhiteSpace(opts.ConfirmLabel)
                ? opts.ConfirmLabel
                : (opts.Mode == ExplorerPickMode.MultiFolder ? S["ExplorerApplyFolders"] : S["ExplorerAddFiles"]));

        dlg = OneUiModalDialog.Show(
            ctx,
            string.IsNullOrWhiteSpace(opts.Title) ? S["ExplorerTitle"] : opts.Title,
            null,
            customContent: root,
            icon: OneUiIconType.Folder,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: opts.Mode == ExplorerPickMode.SingleFile ? null : posLabel,
            onPositive: opts.Mode == ExplorerPickMode.SingleFile
                ? null
                : () => ConfirmResolvedPaths(selectedPaths.ToList()),
            negativeText: S["CancelBtn"]);

        View safTarget = root.FindViewWithTag("saf_btn");
        if (safTarget != null) {
            safTarget.Click += (_, _) => {
                dlg?.Dismiss();
                opts.OnFallbackSystemPicker?.Invoke();
            };
        }

        // Find primary confirm button inside modal to update live selected count
        if (opts.Mode != ExplorerPickMode.SingleFile && dlg?.Window?.DecorView is ViewGroup vg) {
            confirmActionBtn = FindLastButton(vg);
            RefreshSelectionFooter();
        }

        NavigateTo(startDir);
    }

    static Button FindLastButton(ViewGroup parent) {
        Button found = null;
        for (int i = 0; i < parent.ChildCount; i++) {
            View child = parent.GetChildAt(i);
            if (child is Button b) found = b;
            else if (child is ViewGroup sub) {
                Button deeper = FindLastButton(sub);
                if (deeper != null) found = deeper;
            }
        }
        return found;
    }
}
