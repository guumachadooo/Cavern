using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Media;
using Android.Provider;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class LibraryTrackItem {
    public string FilePath { get; set; } = "";
    public string ContentUriString { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public Android.Net.Uri ContentUri {
        get => string.IsNullOrEmpty(ContentUriString) ? null : Android.Net.Uri.Parse(ContentUriString);
        set => ContentUriString = value?.ToString() ?? "";
    }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string FormatBadge { get; set; } = "";
    public long DurationMs { get; set; }
    public long AlbumId { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public bool IsVideo { get; set; }
    public int ChannelCount { get; set; }
    public bool IsSpatial { get; set; }
    public string AudioBadge { get; set; } = "";
    public bool IsLikelyNonMusic { get; set; }
    public string NonMusicReason { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public long LastModifiedTicks { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSurround => IsSpatial || ChannelCount > 2;

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveAlbumArtist => !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist;

    // Key per exact FilePath so tracks in Download/Music or mixed folders NEVER share wrong covers
    [System.Text.Json.Serialization.JsonIgnore]
    public string CacheKey => FilePath;
}

public sealed class LibraryMergedTrack {
    public LibraryTrackItem Primary { get; init; }
    public List<LibraryTrackItem> Variants { get; } = [];
    public string CombinedBadgeLabel { get; set; } = "";
}

public sealed class LibraryAlbumGroup {
    public string Key { get; init; } = "";
    public string AlbumTitle { get; init; } = "";
    public string Artist { get; init; } = "";
    public long AlbumId { get; init; }
    public string SampleTrackPath { get; init; } = "";
    public Android.Net.Uri SampleContentUri { get; init; }
    public List<LibraryTrackItem> Tracks { get; } = [];
}

public sealed class LibraryArtistGroup {
    public string Artist { get; init; } = "";
    public int AlbumCount { get; set; }
    public List<LibraryTrackItem> Tracks { get; } = [];
}

public sealed class LibraryFolderGroup {
    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public List<LibraryTrackItem> Tracks { get; } = [];
}

public sealed class MediaLibrarySnapshot {
    public List<LibraryTrackItem> Tracks { get; init; } = [];
    public List<LibraryAlbumGroup> Albums { get; init; } = [];
    public List<LibraryArtistGroup> Artists { get; init; } = [];
    public List<LibraryFolderGroup> Folders { get; init; } = [];
    public DateTime ScannedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Fast hybrid Samsung Music + Poweramp media library indexer featuring direct MP4/FLAC/ID3 tag reading,
/// rule-based non-music clutter classification, directory inclusion/exclusion filtering,
/// and smart multi-format track variant merging (e.g. FLAC + Dolby Atmos of the same album).
/// </summary>
public static partial class MediaLibraryService {
    public static readonly HashSet<string> SupportedAudioExts = new(StringComparer.OrdinalIgnoreCase) {
        ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".opus",
        ".ac3", ".eac3", ".ec3", ".thd", ".mlp", ".atmos", ".caf", ".laf", ".mka"
    };

    public static readonly HashSet<string> SupportedVideoExts = new(StringComparer.OrdinalIgnoreCase) {
        ".mkv", ".mp4", ".m4v", ".mov", ".webm", ".ts", ".m2ts"
    };

    public static bool IsSupportedMediaExtension(string ext) =>
        !string.IsNullOrEmpty(ext) && (SupportedAudioExts.Contains(ext) || SupportedVideoExts.Contains(ext));

    static readonly string[] ExternalCoverNames = [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.png", "artwork.jpg", "artwork.png"
    ];

    static readonly ConcurrentDictionary<string, Bitmap> thumbCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<string> thumbLru = new();
    static readonly object thumbLock = new();
    static readonly object cacheFileLock = new();
    static MediaLibrarySnapshot cachedSnapshot;

    public static MediaLibrarySnapshot CachedSnapshot => cachedSnapshot;

    const string DiskCacheFileName = "library_cache_v3.json";

    [GeneratedRegex(@"^(?:WA-)?(?:PTT|AUD|VID)[-_]\d{8}[-_]WA\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WhatsAppNameRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{20,}", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UuidOrHexHashRegex();

    [GeneratedRegex(@"^(?:Emergency|Nota\s+\d+_W|Entrevista|Grava[cç][aã]o|Voice|Call|Rec|Audio|IMG|VID|Screen_?Recording)[-_ ]\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RecordingPrefixRegex();

    [GeneratedRegex(@"^(?:\d{8}[_-]\d{6}|\d{2}-\d{2}-\d{2}[,_ ]\d{2}-\d{2}|\d{10,13}[_-]|\d{1,5}$|[-+]?\d+\s*[A-G][#b]?$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TimestampOrCodeNameRegex();

    [GeneratedRegex(@"(?:_mdx(?:23c?|B)?|_dereverb|[-_]mvsep|_all_in_ensemble|_(?:vocalslead|vocalsback|instrum|backing\s*vocals|lead\s*vocals|vocals|karaoke|noreverb)$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StemFragmentRegex();

    [GeneratedRegex(@"\s*\((?:feat\.?|ft\.?|with|featuring)\s+[^)]+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FeaturingClauseRegex();

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Compiled)]
    private static partial Regex SquareBracketsRegex();

    sealed class DiskLibraryCache {
        public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;
        public List<LibraryTrackItem> Tracks { get; set; } = [];
    }

    public static void InvalidateThumbCache(string key = null) {
        lock (thumbLock) {
            if (string.IsNullOrEmpty(key)) {
                thumbCache.Clear();
                thumbLru.Clear();
            } else if (thumbCache.TryRemove(key, out _)) {
                thumbLru.Remove(key);
            }
        }
    }

    public static bool PassesFilter(LibraryTrackItem item, int filterMode) {
        if (item == null) return false;
        return filterMode switch {
            1 => item.IsSurround, // Espacial + Surround (exclui estéreo/mono)
            2 => item.IsSpatial,  // Somente Espacial / Atmos / TrueHD / Objects
            _ => true
        };
    }

    public static bool PassesFolderFilter(string folderPath, int folderFilterMode, IReadOnlyList<string> selectedFolders) {
        if (folderFilterMode == 0 || selectedFolders == null || selectedFolders.Count == 0) {
            return true;
        }
        string normFolder = (folderPath ?? "").TrimEnd('/');
        bool matchesAny = false;
        for (int i = 0; i < selectedFolders.Count; i++) {
            string sel = (selectedFolders[i] ?? "").TrimEnd('/');
            if (string.IsNullOrEmpty(sel)) continue;
            if (normFolder.Equals(sel, StringComparison.OrdinalIgnoreCase) ||
                normFolder.StartsWith(sel + "/", StringComparison.OrdinalIgnoreCase)) {
                matchesAny = true;
                break;
            }
        }
        return folderFilterMode switch {
            1 => !matchesAny, // Todos exceto os escolhidos
            2 => matchesAny,  // Somente os escolhidos
            _ => true
        };
    }

    public static bool PassesAllFilters(
        LibraryTrackItem item,
        int spatialFilterMode,
        bool hideNonMusic,
        int folderFilterMode,
        IReadOnlyList<string> selectedFolders) {
        if (item == null) return false;
        if (!PassesFilter(item, spatialFilterMode)) return false;
        if (hideNonMusic && item.IsLikelyNonMusic) return false;
        if (!PassesFolderFilter(item.FolderPath, folderFilterMode, selectedFolders)) return false;
        return true;
    }

    public static MediaLibrarySnapshot TryLoadCachedSnapshot(Context ctx) {
        if (cachedSnapshot != null && cachedSnapshot.Tracks.Count > 0) {
            return cachedSnapshot;
        }
        try {
            string cachePath = GetDiskCachePath(ctx);
            if (string.IsNullOrEmpty(cachePath) || !IOFile.Exists(cachePath)) {
                return null;
            }
            string json;
            lock (cacheFileLock) {
                json = IOFile.ReadAllText(cachePath);
            }
            DiskLibraryCache disk = JsonSerializer.Deserialize<DiskLibraryCache>(json);
            if (disk?.Tracks == null || disk.Tracks.Count == 0) {
                return null;
            }
            foreach (LibraryTrackItem tr in disk.Tracks) {
                ClassifyTrackRelevance(tr);
            }
            MediaLibrarySnapshot snap = BuildSnapshotFromTracks(disk.Tracks, disk.ScannedAtUtc);
            cachedSnapshot = snap;
            return snap;
        } catch (Exception ex) {
            AppLogger.Error("MediaLibraryService.TryLoadCachedSnapshot", ex);
            return null;
        }
    }

    public static async Task<MediaLibrarySnapshot> ScanAsync(Context ctx, bool forceRefresh = false) {
        var res = await ScanIncrementalAsync(ctx, forceRefresh).ConfigureAwait(false);
        return res.Snapshot;
    }

    public static Task<(MediaLibrarySnapshot Snapshot, int AddedCount, int UpdatedCount, int RemovedCount)> ScanIncrementalAsync(Context ctx, bool forceFullRescan = false) {
        return Task.Run(() => {
            Dictionary<string, LibraryTrackItem> previousByPath = new(StringComparer.OrdinalIgnoreCase);
            MediaLibrarySnapshot prevSnap = cachedSnapshot ?? TryLoadCachedSnapshot(ctx);
            if (prevSnap != null) {
                foreach (LibraryTrackItem old in prevSnap.Tracks) {
                    if (!string.IsNullOrEmpty(old.FilePath)) {
                        previousByPath[old.FilePath] = old;
                    }
                }
            }

            Dictionary<string, LibraryTrackItem> scannedByPath = new(StringComparer.OrdinalIgnoreCase);
            QueryMediaStore(ctx, isVideo: false, scannedByPath);
            QueryMediaStore(ctx, isVideo: true, scannedByPath);
            ScanCommonMediaFolders(scannedByPath);

            int added = 0;
            int updated = 0;
            int removed = 0;
            List<LibraryTrackItem> finalTracks = new(scannedByPath.Count);

            foreach (LibraryTrackItem current in scannedByPath.Values) {
                long len = 0;
                long modTicks = 0;
                try {
                    FileInfo fi = new(current.FilePath);
                    if (fi.Exists) {
                        len = fi.Length;
                        modTicks = fi.LastWriteTimeUtc.Ticks;
                    }
                } catch {
                    // Ignore stat error
                }
                current.FileSizeBytes = len;
                current.LastModifiedTicks = modTicks;

                bool hasPrev = previousByPath.TryGetValue(current.FilePath, out LibraryTrackItem prev);
                bool matchedCache = !forceFullRescan
                    && hasPrev
                    && prev.FileSizeBytes == len
                    && prev.LastModifiedTicks == modTicks
                    && (!string.IsNullOrEmpty(prev.AudioBadge) || prev.ChannelCount > 0 || prev.IsSpatial);

                if (matchedCache) {
                    current.ChannelCount = prev.ChannelCount;
                    current.IsSpatial = prev.IsSpatial;
                    current.AudioBadge = prev.AudioBadge;
                    current.IsVideo = prev.IsVideo;
                    if (prev.DurationMs > 0 && current.DurationMs <= 0) current.DurationMs = prev.DurationMs;
                    if (prev.TrackNumber > 0) current.TrackNumber = prev.TrackNumber;
                    if (prev.DiscNumber > 0) current.DiscNumber = prev.DiscNumber;
                    if (!string.IsNullOrWhiteSpace(prev.AlbumArtist)) current.AlbumArtist = prev.AlbumArtist;
                    if (!string.IsNullOrWhiteSpace(prev.Title)) current.Title = prev.Title;
                    if (!string.IsNullOrWhiteSpace(prev.Artist)) current.Artist = prev.Artist;
                    if (!string.IsNullOrWhiteSpace(prev.Album)) current.Album = prev.Album;
                    ClassifyTrackRelevance(current);
                } else {
                    ProbeTrackAudioProperties(current);
                    if (previousByPath.ContainsKey(current.FilePath)) {
                        updated++;
                    } else {
                        added++;
                    }
                }

                finalTracks.Add(current);
            }

            foreach (string oldPath in previousByPath.Keys) {
                if (!scannedByPath.ContainsKey(oldPath)) {
                    removed++;
                }
            }

            bool cacheMissing = string.IsNullOrEmpty(GetDiskCachePath(ctx)) || !IOFile.Exists(GetDiskCachePath(ctx));
            MediaLibrarySnapshot snap = BuildSnapshotFromTracks(finalTracks, DateTime.UtcNow);
            cachedSnapshot = snap;

            if (forceFullRescan || cacheMissing || added > 0 || updated > 0 || removed > 0) {
                SaveDiskCache(ctx, snap);
            }

            return (snap, added, updated, removed);
        });
    }

    static string GetDiskCachePath(Context ctx) {
        string dir = ctx?.FilesDir?.AbsolutePath;
        return string.IsNullOrEmpty(dir) ? "" : IOPath.Combine(dir, DiskCacheFileName);
    }

    static void SaveDiskCache(Context ctx, MediaLibrarySnapshot snap) {
        try {
            string cachePath = GetDiskCachePath(ctx);
            if (string.IsNullOrEmpty(cachePath) || snap == null) return;
            DiskLibraryCache dto = new() {
                ScannedAtUtc = snap.ScannedAtUtc,
                Tracks = snap.Tracks
            };
            string json = JsonSerializer.Serialize(dto);
            lock (cacheFileLock) {
                IOFile.WriteAllText(cachePath, json);
            }
        } catch (Exception ex) {
            AppLogger.Error("MediaLibraryService.SaveDiskCache", ex);
        }
    }

    static bool IsGenericFolderName(string name) {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Equals("Download", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Music", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Audiobooks", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cavernize", StringComparison.OrdinalIgnoreCase)
            || name.Equals("0", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsGenericRootFolderPath(string folderPath) {
        if (string.IsNullOrWhiteSpace(folderPath)) return true;
        string trimmed = folderPath.TrimEnd('/');
        string last = IOPath.GetFileName(trimmed) ?? "";
        string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/') ?? "/storage/emulated/0";
        return trimmed.Equals(extRoot, StringComparison.OrdinalIgnoreCase) || IsGenericFolderName(last);
    }

    static int CompareTracksByDiscAndNumber(LibraryTrackItem a, LibraryTrackItem b) {
        int da = a.DiscNumber > 0 ? a.DiscNumber : 1;
        int db = b.DiscNumber > 0 ? b.DiscNumber : 1;
        int dc = da.CompareTo(db);
        if (dc != 0) return dc;

        int ta = a.TrackNumber > 0 ? a.TrackNumber : int.MaxValue;
        int tb = b.TrackNumber > 0 ? b.TrackNumber : int.MaxValue;
        int tc = ta.CompareTo(tb);
        if (tc != 0) return tc;

        return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
    }

    static MediaLibrarySnapshot BuildSnapshotFromTracks(List<LibraryTrackItem> rawTracks, DateTime scannedAtUtc) {
        List<LibraryTrackItem> tracks = [.. rawTracks];
        tracks.Sort((a, b) => {
            int ac = string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase);
            return ac != 0 ? ac : CompareTracksByDiscAndNumber(a, b);
        });

        Dictionary<string, LibraryAlbumGroup> albumMap = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LibraryArtistGroup> artistMap = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LibraryFolderGroup> folderMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (LibraryTrackItem t in tracks) {
            bool genericAlbum = string.IsNullOrWhiteSpace(t.Album) || IsGenericFolderName(t.Album);
            string artName = string.IsNullOrWhiteSpace(t.EffectiveAlbumArtist) ? "Artista Desconhecido" : t.EffectiveAlbumArtist;
            string albName;
            string albKey;
            if (genericAlbum) {
                if (!string.IsNullOrWhiteSpace(artName) && artName != "Artista Desconhecido") {
                    albName = $"Singles ({artName})";
                    albKey = $"singles||{artName}";
                } else {
                    albName = string.IsNullOrWhiteSpace(t.Title) ? "Single" : t.Title;
                    albKey = $"track||{t.FilePath}";
                }
            } else {
                albName = t.Album.Trim();
                albKey = $"{NormalizeAlnum(albName)}||{NormalizeAlnum(artName)}";
            }

            if (!albumMap.TryGetValue(albKey, out LibraryAlbumGroup alb)) {
                alb = new LibraryAlbumGroup {
                    Key = albKey,
                    AlbumTitle = albName,
                    Artist = artName,
                    AlbumId = genericAlbum ? -1 : t.AlbumId,
                    SampleTrackPath = t.FilePath,
                    SampleContentUri = t.ContentUri
                };
                albumMap[albKey] = alb;
            }
            alb.Tracks.Add(t);

            string trackArtist = string.IsNullOrWhiteSpace(t.Artist) ? artName : t.Artist;
            if (!artistMap.TryGetValue(trackArtist, out LibraryArtistGroup art)) {
                art = new LibraryArtistGroup { Artist = trackArtist };
                artistMap[trackArtist] = art;
            }
            art.Tracks.Add(t);

            if (!string.IsNullOrEmpty(t.FolderPath)) {
                if (!folderMap.TryGetValue(t.FolderPath, out LibraryFolderGroup fld)) {
                    fld = new LibraryFolderGroup {
                        FolderPath = t.FolderPath,
                        FolderName = t.FolderName
                    };
                    folderMap[t.FolderPath] = fld;
                }
                fld.Tracks.Add(t);
            }
        }

        foreach (LibraryAlbumGroup alb in albumMap.Values) {
            alb.Tracks.Sort(CompareTracksByDiscAndNumber);
        }
        foreach (LibraryArtistGroup art in artistMap.Values) {
            art.AlbumCount = art.Tracks
                .Select(x => string.IsNullOrWhiteSpace(x.Album) || IsGenericFolderName(x.Album) ? x.Title : x.Album)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        return new MediaLibrarySnapshot {
            Tracks = tracks,
            Albums = albumMap.Values.OrderBy(a => a.AlbumTitle, StringComparer.OrdinalIgnoreCase).ToList(),
            Artists = artistMap.Values.OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ToList(),
            Folders = folderMap.Values.OrderBy(f => f.FolderName, StringComparer.OrdinalIgnoreCase).ToList(),
            ScannedAtUtc = scannedAtUtc
        };
    }

    // ============================================================
    // MULTI-FORMAT VARIANT MERGING (e.g. FLAC + Dolby Atmos)
    // ============================================================
    static string NormalizeAlnum(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string decomposed = raw.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(decomposed.Length);
        foreach (char c in decomposed) {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    static string CanonicalTrackBaseTitle(string title) {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string s = SquareBracketsRegex().Replace(title, " ");
        s = FeaturingClauseRegex().Replace(s, " ");
        return NormalizeAlnum(s);
    }

    public static string GetVariantShortLabel(LibraryTrackItem item) {
        if (item == null) return "";
        if (item.IsSpatial) {
            return string.IsNullOrWhiteSpace(item.AudioBadge) ? "ATMOS" : item.AudioBadge;
        }
        if (item.IsSurround) {
            return string.IsNullOrWhiteSpace(item.AudioBadge) ? "SURROUND" : item.AudioBadge;
        }
        return string.IsNullOrWhiteSpace(item.FormatBadge) ? "AUDIO" : item.FormatBadge;
    }

    public static string GetDistinctVariantLabel(LibraryTrackItem item, IReadOnlyList<LibraryTrackItem> allVariants) {
        string baseLabel = GetVariantShortLabel(item);
        if (allVariants == null || allVariants.Count(v => string.Equals(GetVariantShortLabel(v), baseLabel, StringComparison.OrdinalIgnoreCase)) <= 1) {
            return baseLabel;
        }
        string extra = item.IsSpatial && !string.IsNullOrWhiteSpace(item.FormatBadge) && !item.FormatBadge.Equals(baseLabel, StringComparison.OrdinalIgnoreCase)
            ? $"{baseLabel} ({item.FormatBadge})"
            : $"{baseLabel} • {item.AudioBadge}";
        return extra.Trim(' ', '•');
    }

    static int VariantSortPriority(LibraryTrackItem item) {
        if (item == null) return 99;
        if (item.AudioBadge.Contains("TRUEHD", StringComparison.OrdinalIgnoreCase)) return 0;
        if (item.IsSpatial) return 1;
        if (item.IsSurround) return 2;
        if (item.FormatBadge is "FLAC" or "WAV" or "CAF" or "LAF") return 3;
        return 4;
    }

    public static List<LibraryMergedTrack> MergeAlbumTrackVariants(IEnumerable<LibraryTrackItem> sourceTracks) {
        List<LibraryTrackItem> sorted = (sourceTracks ?? []).ToList();
        sorted.Sort(CompareTracksByDiscAndNumber);

        List<LibraryMergedTrack> merged = [];
        Dictionary<string, LibraryMergedTrack> byKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (LibraryTrackItem item in sorted) {
            string baseTitle = CanonicalTrackBaseTitle(item.Title);
            bool isStem = StemFragmentRegex().IsMatch(IOPath.GetFileNameWithoutExtension(item.FilePath));
            string mergeKey;
            if (isStem || string.IsNullOrEmpty(baseTitle)) {
                mergeKey = $"unique||{item.FilePath}";
            } else {
                int disc = item.DiscNumber > 0 ? item.DiscNumber : 1;
                string trkPart = item.TrackNumber > 0 ? $"#{disc}.{item.TrackNumber}||" : "";
                mergeKey = $"{trkPart}{baseTitle}";
            }

            if (!byKey.TryGetValue(mergeKey, out LibraryMergedTrack group)) {
                group = new LibraryMergedTrack { Primary = item };
                byKey[mergeKey] = group;
                merged.Add(group);
            }
            group.Variants.Add(item);
        }

        List<LibraryMergedTrack> finalized = new(merged.Count);
        foreach (LibraryMergedTrack m in merged) {
            m.Variants.Sort((a, b) => {
                int p = VariantSortPriority(a).CompareTo(VariantSortPriority(b));
                return p != 0 ? p : string.Compare(a.FormatBadge, b.FormatBadge, StringComparison.OrdinalIgnoreCase);
            });
            LibraryTrackItem bestPrimary = m.Variants[0];
            List<string> labels = m.Variants
                .Select(GetVariantShortLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string badge = labels.Count > 1 ? string.Join(" + ", labels.Take(2)) + (labels.Count > 2 ? $" +{labels.Count - 2}" : "") : GetVariantShortLabel(bestPrimary);
            LibraryMergedTrack finalItem = new() {
                Primary = bestPrimary,
                CombinedBadgeLabel = badge
            };
            finalItem.Variants.AddRange(m.Variants);
            finalized.Add(finalItem);
        }

        finalized.Sort((a, b) => CompareTracksByDiscAndNumber(a.Primary, b.Primary));
        return finalized;
    }

    public static List<LibraryTrackItem> FindVariantsForTrack(
        MediaLibrarySnapshot snap,
        string filePath,
        string title,
        string artist,
        string album) {
        if (snap == null || string.IsNullOrEmpty(filePath)) return [];
        LibraryTrackItem current = snap.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        string targetTitle = CanonicalTrackBaseTitle(!string.IsNullOrWhiteSpace(current?.Title) ? current.Title : title);
        string targetAlbum = NormalizeAlnum(!string.IsNullOrWhiteSpace(current?.Album) ? current.Album : album);
        string targetArtist = NormalizeAlnum(!string.IsNullOrWhiteSpace(current?.EffectiveAlbumArtist) ? current.EffectiveAlbumArtist : artist);
        if (string.IsNullOrEmpty(targetTitle)) return current != null ? [current] : [];

        bool isCurrentStem = StemFragmentRegex().IsMatch(IOPath.GetFileNameWithoutExtension(filePath));
        if (isCurrentStem) return current != null ? [current] : [];

        List<LibraryTrackItem> matches = snap.Tracks.Where(t => {
            if (StemFragmentRegex().IsMatch(IOPath.GetFileNameWithoutExtension(t.FilePath))) return false;
            if (CanonicalTrackBaseTitle(t.Title) != targetTitle) return false;

            string tAlbum = NormalizeAlnum(t.Album);
            string tArtist = NormalizeAlnum(t.EffectiveAlbumArtist);
            if (!string.IsNullOrEmpty(targetAlbum) && !string.IsNullOrEmpty(tAlbum)) {
                return tAlbum == targetAlbum && (string.IsNullOrEmpty(targetArtist) || string.IsNullOrEmpty(tArtist) || tArtist == targetArtist);
            }
            if (!string.IsNullOrEmpty(targetArtist) && !string.IsNullOrEmpty(tArtist)) {
                return tArtist == targetArtist;
            }
            return string.Equals(t.FolderPath, IOPath.GetDirectoryName(filePath), StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (current != null && !matches.Contains(current)) {
            matches.Add(current);
        }
        matches.Sort((a, b) => {
            int p = VariantSortPriority(a).CompareTo(VariantSortPriority(b));
            return p != 0 ? p : string.Compare(a.FormatBadge, b.FormatBadge, StringComparison.OrdinalIgnoreCase);
        });
        return matches;
    }

    // ============================================================
    // SMART NON-MUSIC CLUTTER CLASSIFIER (NO AI REQUIRED)
    // ============================================================
    static void ClassifyTrackRelevance(LibraryTrackItem item) {
        if (item == null || string.IsNullOrEmpty(item.FilePath)) return;

        string path = item.FilePath.Replace('\\', '/');
        string folder = (item.FolderPath ?? "").Replace('\\', '/');
        string fileBase = IOPath.GetFileNameWithoutExtension(path) ?? "";
        string ext = IOPath.GetExtension(path).ToLowerInvariant();

        // System / App SFX sounds are always noise regardless of channel count
        if (path.Contains("/res/raw/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Câmera.ver", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Sounds/Gear/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Cubasis 3/Trash/", StringComparison.OrdinalIgnoreCase)) {
            item.IsLikelyNonMusic = true;
            item.NonMusicReason = "Som de sistema / app";
            return;
        }

        // HARD KEEP: Spatial / Surround audio (including Atmos talks/lectures, spatial demos, Atmos movies)
        if (item.IsSpatial || item.IsSurround || item.ChannelCount > 2) {
            item.IsLikelyNonMusic = false;
            item.NonMusicReason = "";
            return;
        }

        // Separated AI stems / karaoke / dereverb fragments (MVSEP, etc.)
        if (StemFragmentRegex().IsMatch(fileBase) ||
            folder.Contains("/Projeto_Stems_Separados", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/AudioCutter", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/Mp3Cutter", StringComparison.OrdinalIgnoreCase)) {
            item.IsLikelyNonMusic = true;
            item.NonMusicReason = "Stem / recorte de áudio";
            return;
        }

        bool hasMusicTags = (!string.IsNullOrWhiteSpace(item.Artist) && !IsGenericFolderName(item.Artist)) ||
            (!string.IsNullOrWhiteSpace(item.Album) && !IsGenericFolderName(item.Album)) ||
            item.TrackNumber > 0;

        // WhatsApp / Voice / Call recording folders
        if (folder.Contains("/WhatsApp", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/WhatsMediaConv", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/Recordings/Call", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/Voice Recorder", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/gravações de voz", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/gravacoes de voz", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains("/SoundRecorder", StringComparison.OrdinalIgnoreCase)) {
            if (!hasMusicTags) {
                item.IsLikelyNonMusic = true;
                item.NonMusicReason = "Gravação / WhatsApp";
                return;
            }
        }

        // Social media & screen-recording video dumps without album/artist tags
        if (item.IsVideo && !hasMusicTags &&
            (folder.Contains("/InstaFlow", StringComparison.OrdinalIgnoreCase) ||
             folder.Contains("/InstaPrime", StringComparison.OrdinalIgnoreCase) ||
             folder.Contains("/Instagram", StringComparison.OrdinalIgnoreCase) ||
             folder.Contains("/TikTok", StringComparison.OrdinalIgnoreCase) ||
             folder.Contains("/Snapchat", StringComparison.OrdinalIgnoreCase) ||
             folder.Contains("/AzScreenRecorder", StringComparison.OrdinalIgnoreCase) ||
             folder.Contains("/Collage", StringComparison.OrdinalIgnoreCase))) {
            item.IsLikelyNonMusic = true;
            item.NonMusicReason = "Vídeo de rede social / captura";
            return;
        }

        // Generic / random hash / UUID / timestamp / voice filename patterns (unless properly tagged as music!)
        if (!hasMusicTags) {
            string folderLeaf = IOPath.GetFileName(folder.TrimEnd('/')) ?? "";
            if (WhatsAppNameRegex().IsMatch(fileBase) ||
                RecordingPrefixRegex().IsMatch(fileBase) ||
                TimestampOrCodeNameRegex().IsMatch(fileBase) ||
                UuidOrHexHashRegex().IsMatch(fileBase) ||
                (folderLeaf.Length >= 32 && UuidOrHexHashRegex().IsMatch(folderLeaf))) {
                item.IsLikelyNonMusic = true;
                item.NonMusicReason = "Nome genérico / gravação";
                return;
            }

            if (ext == ".amr") {
                item.IsLikelyNonMusic = true;
                item.NonMusicReason = "Nota de voz (.amr)";
                return;
            }

            if (item.DurationMs > 0 && item.DurationMs < 25_000) {
                item.IsLikelyNonMusic = true;
                item.NonMusicReason = "Clipe curto sem metadados";
                return;
            }
        }

        item.IsLikelyNonMusic = false;
        item.NonMusicReason = "";
    }

    static void ProbeTrackAudioProperties(LibraryTrackItem item) {
        if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
        string ext = IOPath.GetExtension(item.FilePath).ToLowerInvariant();
        string lowerName = IOPath.GetFileName(item.FilePath).ToLowerInvariant();

        bool extSpatial = ext is ".atmos" or ".thd" or ".mlp" or ".ec3" or ".eac3" or ".caf" or ".laf";
        bool extSurround = extSpatial || ext is ".ac3" or ".dts";
        bool tagSpatial = lowerName.Contains("atmos") || lowerName.Contains("spatial")
            || lowerName.Contains("eac3-joc") || lowerName.Contains("joc")
            || lowerName.Contains("360ra") || lowerName.Contains("truehd")
            || lowerName.Contains("adm");

        int ch = 0;
        string detectedMime = "";
        bool sawAudio = false;
        bool sawVideo = false;
        bool wavAdm = false;

        if (ext == ".wav") {
            InspectWavHeader(item.FilePath, out ch, out wavAdm);
        }

        if (IOFile.Exists(item.FilePath)) {
            try {
                using MediaExtractor extractor = new();
                extractor.SetDataSource(item.FilePath);
                int count = extractor.TrackCount;
                for (int i = 0; i < count; i++) {
                    MediaFormat fmt = extractor.GetTrackFormat(i);
                    string mime = fmt.GetString(MediaFormat.KeyMime) ?? "";
                    if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) {
                        sawVideo = true;
                        continue;
                    }
                    if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!sawAudio) {
                        sawAudio = true;
                        detectedMime = mime.ToLowerInvariant();
                        if (ch <= 0 && fmt.ContainsKey(MediaFormat.KeyChannelCount)) {
                            ch = fmt.GetInteger(MediaFormat.KeyChannelCount);
                        }
                        if (item.DurationMs <= 0 && fmt.ContainsKey(MediaFormat.KeyDuration)) {
                            long us = fmt.GetLong(MediaFormat.KeyDuration);
                            if (us > 0) item.DurationMs = us / 1000L;
                        }
                    }
                }
                if (sawAudio && !sawVideo) {
                    item.IsVideo = false;
                } else if (sawVideo) {
                    item.IsVideo = true;
                }
            } catch {
                // Unsupported container for MediaExtractor (e.g. raw .thd/.mlp/.caf) -> handled via ext flags below
            }

            // Read native MP4/M4A/FLAC/ID3 tags (fixes missing track numbers & albums on MP4 Atmos!)
            ParsedAudioTags binTags = AudioTagReader.TryReadTags(item.FilePath);
            if (binTags != null) {
                if (binTags.TrackNumber > 0) item.TrackNumber = binTags.TrackNumber;
                if (binTags.DiscNumber > 0) item.DiscNumber = binTags.DiscNumber;
                if (!string.IsNullOrWhiteSpace(binTags.AlbumArtist)) item.AlbumArtist = binTags.AlbumArtist;
                if (!string.IsNullOrWhiteSpace(binTags.Artist)) item.Artist = binTags.Artist;
                if (!string.IsNullOrWhiteSpace(binTags.Album) && !IsGenericFolderName(binTags.Album)) {
                    item.Album = binTags.Album;
                }
                if (!string.IsNullOrWhiteSpace(binTags.Title)) {
                    item.Title = binTags.Title;
                }
            }
        }

        bool mimeSpatial = detectedMime.Contains("joc") || detectedMime.Contains("eac3")
            || detectedMime.Contains("truehd") || detectedMime.Contains("mlp")
            || detectedMime.Contains("mha") || detectedMime.Contains("360");
        bool mimeSurround = mimeSpatial || detectedMime.Contains("ac3") || detectedMime.Contains("dts");

        if (ch <= 0) {
            if (extSpatial) ch = 8;
            else if (extSurround) ch = 6;
            else ch = 2;
        }

        bool isSpatial = extSpatial || wavAdm || mimeSpatial || (tagSpatial && ch >= 2) || ch >= 8;
        bool isSurround = isSpatial || extSurround || mimeSurround || ch > 2;

        item.ChannelCount = ch;
        item.IsSpatial = isSpatial;

        if (isSpatial) {
            if (ext == ".atmos" || detectedMime.Contains("joc") || tagSpatial) {
                item.AudioBadge = "ATMOS";
            } else if (ext is ".thd" or ".mlp" || detectedMime.Contains("truehd")) {
                item.AudioBadge = "TRUEHD";
            } else if (ext is ".ec3" or ".eac3" || detectedMime.Contains("eac3")) {
                item.AudioBadge = "E-AC3";
            } else if (wavAdm) {
                item.AudioBadge = "ADM 3D";
            } else {
                item.AudioBadge = ch > 2 ? $"{ch}CH 3D" : "ESPACIAL";
            }
        } else if (isSurround) {
            if (ext == ".ac3" || detectedMime.Contains("ac3")) {
                item.AudioBadge = "AC3 5.1";
            } else if (ext == ".dts" || detectedMime.Contains("dts")) {
                item.AudioBadge = "DTS";
            } else if (ch == 6) {
                item.AudioBadge = "5.1";
            } else if (ch == 4) {
                item.AudioBadge = "4.0";
            } else {
                item.AudioBadge = $"{ch}CH";
            }
        } else {
            item.AudioBadge = ch == 1 ? "MONO" : "2.0";
        }

        ClassifyTrackRelevance(item);
    }

    static void InspectWavHeader(string filePath, out int channels, out bool isAdmBwf) {
        channels = 0;
        isAdmBwf = false;
        try {
            using FileStream fs = IOFile.OpenRead(filePath);
            byte[] buf = new byte[Math.Min(65536, (int)Math.Min(fs.Length, 65536))];
            int read = fs.Read(buf, 0, buf.Length);
            if (read < 44) return;
            if (buf[0] != 'R' || buf[1] != 'I' || buf[2] != 'F' || buf[3] != 'F') return;
            int offset = 12;
            while (offset + 8 <= read) {
                string chunkId = Encoding.ASCII.GetString(buf, offset, 4);
                int chunkSize = BitConverter.ToInt32(buf, offset + 4);
                if (chunkId == "fmt " && offset + 12 <= read) {
                    channels = BitConverter.ToUInt16(buf, offset + 10);
                } else if (chunkId is "axml" or "chna" or "bext") {
                    if (chunkId is "axml" or "chna") isAdmBwf = true;
                }
                if (chunkSize < 0) break;
                offset += 8 + chunkSize + (chunkSize & 1);
            }
        } catch {
            // Ignore malformed header
        }
    }

    static void QueryMediaStore(Context ctx, bool isVideo, Dictionary<string, LibraryTrackItem> byPath) {
        if (ctx?.ContentResolver == null) return;
        try {
            Android.Net.Uri baseUri = isVideo
                ? MediaStore.Video.Media.ExternalContentUri
                : MediaStore.Audio.Media.ExternalContentUri;
            string[] projection = isVideo
                ? ["_id", "title", "_display_name", "duration", "_data"]
                : ["_id", "title", "artist", "album", "album_id", "duration", "track", "_data", "_display_name"];

            using ICursor cursor = ctx.ContentResolver.Query(baseUri, projection, null, null, null);
            if (cursor == null) return;

            int idCol = cursor.GetColumnIndex("_id");
            int titleCol = cursor.GetColumnIndex("title");
            int artistCol = cursor.GetColumnIndex("artist");
            int albumCol = cursor.GetColumnIndex("album");
            int albumIdCol = cursor.GetColumnIndex("album_id");
            int durCol = cursor.GetColumnIndex("duration");
            int trkCol = cursor.GetColumnIndex("track");
            int dataCol = cursor.GetColumnIndex("_data");
            int dispCol = cursor.GetColumnIndex("_display_name");

            while (cursor.MoveToNext()) {
                string path = dataCol >= 0 ? (cursor.GetString(dataCol) ?? "") : "";
                if (string.IsNullOrEmpty(path) || IsIgnoredSystemPath(path)) continue;

                long dur = durCol >= 0 ? cursor.GetLong(durCol) : 0;
                if (!isVideo && dur > 0 && dur < 4500) continue;

                long id = idCol >= 0 ? cursor.GetLong(idCol) : -1;
                Android.Net.Uri itemUri = id >= 0 ? ContentUris.WithAppendedId(baseUri, id) : null;
                string disp = dispCol >= 0 ? cursor.GetString(dispCol) : IOPath.GetFileName(path);
                string title = titleCol >= 0 ? cursor.GetString(titleCol) : "";
                if (string.IsNullOrWhiteSpace(title)) title = IOPath.GetFileNameWithoutExtension(disp ?? path);

                string artist = artistCol >= 0 ? (cursor.GetString(artistCol) ?? "") : "";
                if ("<unknown>".Equals(artist, StringComparison.OrdinalIgnoreCase)) artist = "";

                string album = albumCol >= 0 ? (cursor.GetString(albumCol) ?? "") : "";
                if ("<unknown>".Equals(album, StringComparison.OrdinalIgnoreCase) || IsGenericFolderName(album)) {
                    album = "";
                }

                long albId = albumIdCol >= 0 ? cursor.GetLong(albumIdCol) : -1;
                int trkRaw = trkCol >= 0 ? cursor.GetInt(trkCol) : 0;
                int trkDisc = trkRaw > 1000 ? trkRaw / 1000 : 0;
                int trkNum = trkRaw > 1000 ? trkRaw % 1000 : trkRaw;
                string dir = IOPath.GetDirectoryName(path) ?? "";
                string ext = IOPath.GetExtension(path).TrimStart('.').ToUpperInvariant();

                byPath[path] = new LibraryTrackItem {
                    FilePath = path,
                    ContentUri = itemUri,
                    Title = title.Trim(),
                    Artist = artist.Trim(),
                    Album = album.Trim(),
                    FolderPath = dir,
                    FolderName = IOPath.GetFileName(dir),
                    FormatBadge = string.IsNullOrEmpty(ext) ? (isVideo ? "VIDEO" : "AUDIO") : ext,
                    DurationMs = dur,
                    AlbumId = albId,
                    TrackNumber = trkNum,
                    DiscNumber = trkDisc,
                    IsVideo = isVideo
                };
            }
        } catch (Exception ex) {
            AppLogger.Error($"MediaLibraryService.QueryMediaStore(video={isVideo})", ex);
        }
    }

    static void ScanCommonMediaFolders(Dictionary<string, LibraryTrackItem> byPath) {
        string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        string[] roots = [
            IOPath.Combine(extRoot, "Music"),
            IOPath.Combine(extRoot, "Download"),
            IOPath.Combine(extRoot, "Movies"),
            IOPath.Combine(extRoot, "Audiobooks")
        ];

        foreach (string root in roots) {
            if (!Directory.Exists(root)) continue;
            WalkFolder(root, depth: 0, maxDepth: 5, byPath);
        }
    }

    static void WalkFolder(string dir, int depth, int maxDepth, Dictionary<string, LibraryTrackItem> byPath) {
        if (depth > maxDepth || IsIgnoredSystemPath(dir)) return;
        try {
            foreach (string file in Directory.EnumerateFiles(dir)) {
                string ext = IOPath.GetExtension(file);
                if (string.IsNullOrEmpty(ext)) continue;
                bool isAudio = SupportedAudioExts.Contains(ext);
                bool isVideo = SupportedVideoExts.Contains(ext);
                if (!isAudio && !isVideo) continue;
                if (byPath.ContainsKey(file) || IsIgnoredSystemPath(file)) continue;

                string baseName = IOPath.GetFileNameWithoutExtension(file);
                string folderName = IOPath.GetFileName(dir) ?? "";
                ParseHeuristicNames(baseName, folderName, out string artist, out string title, out string album, out int discNum, out int trkNum);

                byPath[file] = new LibraryTrackItem {
                    FilePath = file,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    FolderPath = dir,
                    FolderName = folderName,
                    FormatBadge = ext.TrimStart('.').ToUpperInvariant(),
                    DiscNumber = discNum,
                    TrackNumber = trkNum,
                    IsVideo = isVideo
                };
            }

            foreach (string sub in Directory.EnumerateDirectories(dir)) {
                string name = IOPath.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
                WalkFolder(sub, depth + 1, maxDepth, byPath);
            }
        } catch {
            // Skip restricted subfolders cleanly
        }
    }

    static void ParseHeuristicNames(string fileBase, string folderName, out string artist, out string title, out string album, out int discNum, out int trkNum) {
        artist = "";
        album = IsGenericFolderName(folderName) ? "" : folderName;
        AudioTagReader.ParseTrackFromFilename(fileBase, out discNum, out trkNum, out string stripped);
        title = stripped;

        int dash = stripped.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 1 && dash + 3 < stripped.Length) {
            string left = stripped[..dash].Trim();
            string right = stripped[(dash + 3)..].Trim();
            if (int.TryParse(left, out int parsedNum) && parsedNum is >= 1 and <= 250) {
                if (trkNum <= 0) trkNum = parsedNum;
                title = right;
            } else {
                artist = left;
                title = right;
            }
        }
    }

    static bool IsIgnoredSystemPath(string path) {
        if (string.IsNullOrEmpty(path)) return true;
        return path.Contains("/Android/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/.", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Alarms/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Ringtones/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Notifications/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/WhatsApp/Media/WhatsApp Voice", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Cavernize/Logs/", StringComparison.OrdinalIgnoreCase);
    }

    public static Bitmap TryGetCachedThumbnail(string cacheKey) {
        if (string.IsNullOrEmpty(cacheKey)) return null;
        if (thumbCache.TryGetValue(cacheKey, out Bitmap bmp) && bmp != null) {
            try {
                if (bmp.Handle != IntPtr.Zero && !bmp.IsRecycled) {
                    return bmp;
                }
            } catch {
                // Evict stale reference below
            }
            thumbCache.TryRemove(cacheKey, out _);
        }
        return null;
    }

    public static Task<Bitmap> LoadThumbnailAsync(Context ctx, LibraryTrackItem item, int sizePx = 240) {
        if (item == null) return Task.FromResult<Bitmap>(null);
        string key = item.CacheKey;
        Bitmap cached = TryGetCachedThumbnail(key);
        if (cached != null) return Task.FromResult(cached);

        return Task.Run(() => {
            Bitmap loaded = null;
            try {
                // 1. Embedded artwork inside the exact media file FIRST (prevents cross-song / folder cover mix-ups)
                if (!string.IsNullOrEmpty(item.FilePath) && IOFile.Exists(item.FilePath)) {
                    using MediaMetadataRetriever mmr = new();
                    try {
                        mmr.SetDataSource(item.FilePath);
                        byte[] bytes = mmr.GetEmbeddedPicture();
                        if (bytes != null && bytes.Length > 0) {
                            loaded = DecodeSampledBytes(bytes, sizePx);
                        } else if (item.IsVideo) {
                            using Bitmap frame = mmr.GetFrameAtTime(3_000_000, Option.ClosestSync);
                            if (frame != null) {
                                loaded = ScaleBitmapCopy(frame, sizePx);
                            }
                        }
                    } catch {
                        // Ignore unsupported retriever containers
                    }
                }

                // 2. Online-enriched persistent cover cache (keyed safely)
                if (loaded == null) {
                    string cachedCoverPath = OnlineMetadataService.GetCachedCoverPath(ctx, item.Title, item.Artist, item.Album, item.FilePath);
                    if (!string.IsNullOrEmpty(cachedCoverPath) && IOFile.Exists(cachedCoverPath)) {
                        loaded = DecodeSampledFile(cachedCoverPath, sizePx);
                    }
                }

                // 3. Dedicated folder artwork ONLY in real album subfolders (never root Download/Music/Movies!)
                if (loaded == null && !IsGenericRootFolderPath(item.FolderPath) && Directory.Exists(item.FolderPath)) {
                    foreach (string name in ExternalCoverNames) {
                        string fp = IOPath.Combine(item.FolderPath, name);
                        if (IOFile.Exists(fp)) {
                            loaded = DecodeSampledFile(fp, sizePx);
                            if (loaded != null) break;
                        }
                    }
                }

                // 4. Android Q+ MediaStore LoadThumbnail ONLY when the item has a real album or is video
                if (loaded == null && item.ContentUri != null && OperatingSystem.IsAndroidVersionAtLeast(29) && ctx?.ContentResolver != null
                    && (item.IsVideo || !IsGenericFolderName(item.Album))) {
                    try {
                        loaded = ctx.ContentResolver.LoadThumbnail(item.ContentUri, new Android.Util.Size(sizePx, sizePx), null);
                    } catch {
                        // Thumbnail may not exist in MediaStore
                    }
                }
            } catch {
                // Ignore thumbnail decode error
            }

            if (loaded != null) {
                StoreThumbInLru(key, loaded);
            }
            return loaded;
        });
    }

    static Bitmap ScaleBitmapCopy(Bitmap source, int maxDim) {
        if (source == null || source.IsRecycled) return null;
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maxDim) {
            return source.Copy(Bitmap.Config.Argb8888!, false);
        }
        float sc = maxDim / (float)longest;
        return Bitmap.CreateScaledBitmap(source, Math.Max(1, (int)(source.Width * sc)), Math.Max(1, (int)(source.Height * sc)), true);
    }

    static void StoreThumbInLru(string key, Bitmap bmp) {
        lock (thumbLock) {
            if (thumbCache.ContainsKey(key)) return;
            thumbCache[key] = bmp;
            thumbLru.AddLast(key);
            while (thumbLru.Count > 96) {
                string oldest = thumbLru.First.Value;
                thumbLru.RemoveFirst();
                thumbCache.TryRemove(oldest, out _);
            }
        }
    }

    static Bitmap DecodeSampledFile(string path, int maxDim) {
        try {
            byte[] bytes = IOFile.ReadAllBytes(path);
            return DecodeSampledBytes(bytes, maxDim);
        } catch {
            return null;
        }
    }

    static Bitmap DecodeSampledBytes(byte[] bytes, int maxDim) {
        if (bytes == null || bytes.Length == 0) return null;
        BitmapFactory.Options bounds = new() { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

        int sample = 1;
        while ((bounds.OutWidth / sample) > maxDim * 2 || (bounds.OutHeight / sample) > maxDim * 2) {
            sample *= 2;
        }
        BitmapFactory.Options opts = new() {
            InSampleSize = sample,
            InPreferredConfig = Bitmap.Config.Argb8888
        };
        Bitmap decoded = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, opts);
        if (decoded == null) return null;

        int longest = Math.Max(decoded.Width, decoded.Height);
        if (longest <= maxDim) return decoded;
        float sc = maxDim / (float)longest;
        Bitmap scaled = Bitmap.CreateScaledBitmap(decoded, Math.Max(1, (int)(decoded.Width * sc)), Math.Max(1, (int)(decoded.Height * sc)), true);
        if (!ReferenceEquals(scaled, decoded)) decoded.Dispose();
        return scaled;
    }
}
