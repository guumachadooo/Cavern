using System.Collections.Concurrent;

using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Provider;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class LibraryTrackItem {
    public string FilePath { get; init; } = "";
    public Android.Net.Uri ContentUri { get; init; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string FormatBadge { get; init; } = "";
    public long DurationMs { get; init; }
    public long AlbumId { get; init; }
    public int TrackNumber { get; init; }
    public bool IsVideo { get; init; }

    public string CacheKey => !string.IsNullOrEmpty(Album) && !string.Equals(Album, "Desconhecido", StringComparison.OrdinalIgnoreCase)
        ? $"{Artist}|{Album}"
        : FilePath;
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
/// Fast hybrid Samsung Music (MediaStore Albums/Tracks/Artists) + Poweramp (direct filesystem spatial format scan)
/// media library indexer and thumbnail cache for the in-app Player library browser.
/// </summary>
public static class MediaLibraryService {
    static readonly HashSet<string> SupportedAudioExts = new(StringComparer.OrdinalIgnoreCase) {
        ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".opus",
        ".ac3", ".eac3", ".ec3", ".thd", ".mlp", ".atmos", ".caf", ".laf", ".mka"
    };

    static readonly HashSet<string> SupportedVideoExts = new(StringComparer.OrdinalIgnoreCase) {
        ".mkv", ".mp4", ".m4v", ".mov", ".webm", ".ts", ".m2ts"
    };

    static readonly string[] ExternalCoverNames = [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.png", "artwork.jpg", "artwork.png"
    ];

    static readonly ConcurrentDictionary<string, Bitmap> thumbCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<string> thumbLru = new();
    static readonly object thumbLock = new();
    static MediaLibrarySnapshot cachedSnapshot;

    public static MediaLibrarySnapshot CachedSnapshot => cachedSnapshot;

    public static void InvalidateThumbCache(string key = null) {
        lock (thumbLock) {
            if (string.IsNullOrEmpty(key)) {
                foreach (var kv in thumbCache) {
                    kv.Value?.Recycle();
                    kv.Value?.Dispose();
                }
                thumbCache.Clear();
                thumbLru.Clear();
            } else if (thumbCache.TryRemove(key, out Bitmap bmp)) {
                thumbLru.Remove(key);
                bmp?.Recycle();
                bmp?.Dispose();
            }
        }
    }

    public static Task<MediaLibrarySnapshot> ScanAsync(Context ctx, bool forceRefresh = false) {
        if (!forceRefresh && cachedSnapshot != null &&
            (DateTime.UtcNow - cachedSnapshot.ScannedAtUtc).TotalMinutes < 10 &&
            cachedSnapshot.Tracks.Count > 0) {
            return Task.FromResult(cachedSnapshot);
        }

        return Task.Run(() => {
            Dictionary<string, LibraryTrackItem> byPath = new(StringComparer.OrdinalIgnoreCase);

            QueryMediaStore(ctx, isVideo: false, byPath);
            QueryMediaStore(ctx, isVideo: true, byPath);
            ScanCommonMediaFolders(byPath);

            List<LibraryTrackItem> tracks = byPath.Values
                .OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Dictionary<string, LibraryAlbumGroup> albumMap = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, LibraryArtistGroup> artistMap = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, LibraryFolderGroup> folderMap = new(StringComparer.OrdinalIgnoreCase);

            foreach (LibraryTrackItem t in tracks) {
                string albName = string.IsNullOrWhiteSpace(t.Album) ? (string.IsNullOrEmpty(t.FolderName) ? "Singles" : t.FolderName) : t.Album;
                string artName = string.IsNullOrWhiteSpace(t.Artist) ? "Artista Desconhecido" : t.Artist;
                string albKey = $"{albName}||{artName}";

                if (!albumMap.TryGetValue(albKey, out LibraryAlbumGroup alb)) {
                    alb = new LibraryAlbumGroup {
                        Key = albKey,
                        AlbumTitle = albName,
                        Artist = artName,
                        AlbumId = t.AlbumId,
                        SampleTrackPath = t.FilePath,
                        SampleContentUri = t.ContentUri
                    };
                    albumMap[albKey] = alb;
                }
                alb.Tracks.Add(t);

                if (!artistMap.TryGetValue(artName, out LibraryArtistGroup art)) {
                    art = new LibraryArtistGroup { Artist = artName };
                    artistMap[artName] = art;
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
                alb.Tracks.Sort((a, b) => {
                    int tc = a.TrackNumber.CompareTo(b.TrackNumber);
                    return tc != 0 ? tc : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                });
            }
            foreach (LibraryArtistGroup art in artistMap.Values) {
                art.AlbumCount = art.Tracks
                    .Select(x => string.IsNullOrWhiteSpace(x.Album) ? x.FolderName : x.Album)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }

            MediaLibrarySnapshot snap = new() {
                Tracks = tracks,
                Albums = albumMap.Values.OrderBy(a => a.AlbumTitle, StringComparer.OrdinalIgnoreCase).ToList(),
                Artists = artistMap.Values.OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ToList(),
                Folders = folderMap.Values.OrderBy(f => f.FolderName, StringComparer.OrdinalIgnoreCase).ToList(),
                ScannedAtUtc = DateTime.UtcNow
            };
            cachedSnapshot = snap;
            return snap;
        });
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
                if ("<unknown>".Equals(album, StringComparison.OrdinalIgnoreCase)) album = "";

                long albId = albumIdCol >= 0 ? cursor.GetLong(albumIdCol) : -1;
                int trkRaw = trkCol >= 0 ? cursor.GetInt(trkCol) : 0;
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
                ParseHeuristicNames(baseName, folderName, out string artist, out string title, out string album);

                byPath[file] = new LibraryTrackItem {
                    FilePath = file,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    FolderPath = dir,
                    FolderName = folderName,
                    FormatBadge = ext.TrimStart('.').ToUpperInvariant(),
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

    static void ParseHeuristicNames(string fileBase, string folderName, out string artist, out string title, out string album) {
        artist = "";
        title = fileBase;
        album = folderName;

        int dash = fileBase.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 1 && dash + 3 < fileBase.Length) {
            string left = fileBase[..dash].Trim();
            string right = fileBase[(dash + 3)..].Trim();
            // Handle "02 - Song Title" vs "Artist - Song Title"
            if (int.TryParse(left, out _)) {
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
            || path.Contains("/Recordings/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Cavernize/Logs/", StringComparison.OrdinalIgnoreCase);
    }

    public static Bitmap TryGetCachedThumbnail(string cacheKey) {
        if (string.IsNullOrEmpty(cacheKey)) return null;
        if (thumbCache.TryGetValue(cacheKey, out Bitmap bmp) && bmp != null && !bmp.IsRecycled) {
            return bmp;
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
                // 1. Check online-enriched persistent cover cache first
                string cachedCoverPath = OnlineMetadataService.GetCachedCoverPath(ctx, item.Title, item.Artist, item.Album);
                if (!string.IsNullOrEmpty(cachedCoverPath) && IOFile.Exists(cachedCoverPath)) {
                    loaded = DecodeSampledFile(cachedCoverPath, sizePx);
                }

                // 2. Folder cover image
                if (loaded == null && !string.IsNullOrEmpty(item.FolderPath) && Directory.Exists(item.FolderPath)) {
                    foreach (string name in ExternalCoverNames) {
                        string fp = IOPath.Combine(item.FolderPath, name);
                        if (IOFile.Exists(fp)) {
                            loaded = DecodeSampledFile(fp, sizePx);
                            if (loaded != null) break;
                        }
                    }
                }

                // 3. Embedded art via MediaMetadataRetriever
                if (loaded == null && !string.IsNullOrEmpty(item.FilePath) && IOFile.Exists(item.FilePath)) {
                    using MediaMetadataRetriever mmr = new();
                    try {
                        mmr.SetDataSource(item.FilePath);
                        byte[] bytes = mmr.GetEmbeddedPicture();
                        if (bytes != null && bytes.Length > 0) {
                            loaded = DecodeSampledBytes(bytes, sizePx);
                        }
                    } catch {
                        // Ignore unsupported retriever containers
                    }
                }

                // 4. Android Q+ MediaStore LoadThumbnail
                if (loaded == null && item.ContentUri != null && OperatingSystem.IsAndroidVersionAtLeast(29) && ctx?.ContentResolver != null) {
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

    static void StoreThumbInLru(string key, Bitmap bmp) {
        lock (thumbLock) {
            if (thumbCache.ContainsKey(key)) return;
            thumbCache[key] = bmp;
            thumbLru.AddLast(key);
            while (thumbLru.Count > 72) {
                string oldest = thumbLru.First.Value;
                thumbLru.RemoveFirst();
                if (thumbCache.TryRemove(oldest, out Bitmap oldBmp)) {
                    oldBmp?.Recycle();
                    oldBmp?.Dispose();
                }
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
        if (!ReferenceEquals(scaled, decoded)) decoded.Recycle();
        return scaled;
    }
}
