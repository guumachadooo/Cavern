using System.Collections.Concurrent;

using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Media;
using Android.Provider;

using CavernizeAndroid.Language;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

public static partial class MediaLibraryService {
    static readonly string[] ExternalCoverNames = [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.png", "artwork.jpg", "artwork.png"
    ];

    static readonly ConcurrentDictionary<string, Bitmap> thumbCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, Task<Bitmap>> inflightThumbs = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim thumbSemaphore = new(3, 3);
    static readonly LinkedList<string> thumbLru = new();
    static readonly object thumbLock = new();

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

    static void ProbeTrackAudioProperties(Context ctx, LibraryTrackItem item) =>
        ProbeTrackAudioPropertiesInternal(ctx, item, SmartCleanRuleSnapshot.FromStore(null));

    static void ProbeTrackAudioPropertiesInternal(Context ctx, LibraryTrackItem item, SmartCleanRuleSnapshot rules) {
        if (item == null || (string.IsNullOrEmpty(item.FilePath) && item.ContentUri == null)) return;
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        Android.Net.Uri effectiveUri = item.ContentUri ?? TryParseUri(item.FilePath);
        bool isUriPath = IsUriString(item.FilePath);

        string fileName = !isUriPath ? IOPath.GetFileName(item.FilePath ?? "") : "";
        string ext = !isUriPath ? IOPath.GetExtension(item.FilePath ?? "").ToLowerInvariant() : "";
        if ((string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(fileName)) && effectiveUri != null && effectiveCtx != null) {
            string disp = AndroidFileHelper.GetDisplayName(effectiveCtx, effectiveUri);
            if (!string.IsNullOrEmpty(disp)) {
                if (string.IsNullOrEmpty(fileName)) fileName = disp;
                if (string.IsNullOrEmpty(ext)) ext = IOPath.GetExtension(disp).ToLowerInvariant();
            }
        }
        if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(item.FormatBadge)) {
            ext = "." + item.FormatBadge.TrimStart('.').ToLowerInvariant();
        }
        string lowerName = (!string.IsNullOrEmpty(fileName) ? fileName : (item.Title ?? "")).ToLowerInvariant();

        bool extSpatial = ext is ".atmos" or ".thd" or ".mlp" or ".ec3" or ".eac3" or ".caf" or ".laf";
        bool extSurround = extSpatial || ext is ".ac3" or ".dts";
        bool hasJocToken = lowerName.Contains("eac3-joc") || SpatialNameJocRegex().IsMatch(lowerName);
        bool hasAdmToken = SpatialNameAdmRegex().IsMatch(lowerName);
        bool tagAtmos = lowerName.Contains("atmos") || hasJocToken;
        bool tagSpatial = tagAtmos || lowerName.Contains("spatial")
            || lowerName.Contains("360ra") || lowerName.Contains("truehd")
            || hasAdmToken;

        int ch = 0;
        string detectedMime = "";
        bool sawAudio = false;
        bool sawVideo = false;
        bool wavAdm = false;
        item.HasEmbeddedMusicTags = false;

        if (ext == ".wav") {
            InspectWavHeader(effectiveCtx, item, out ch, out wavAdm, out long wavDurMs);
            if (item.DurationMs <= 0 && wavDurMs > 0) {
                item.DurationMs = wavDurMs;
            }
        }

        bool directReadable = CanOpenFileForRead(item.FilePath);
        string tagPath = null;
        if (directReadable || (effectiveUri != null && effectiveCtx != null)) {
            MediaExtractor extractor = null;
            try {
                extractor = new MediaExtractor();
                if (directReadable) {
                    extractor.SetDataSource(item.FilePath);
                } else {
                    extractor.SetDataSource(effectiveCtx, effectiveUri, null);
                }
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
                    item.HasNoAudioTrack = false;
                } else if (sawVideo) {
                    item.IsVideo = true;
                    item.HasNoAudioTrack = !sawAudio;
                }
            } catch {
                // Unsupported container for MediaExtractor (e.g. raw .thd/.mlp/.caf) -> handled via ext flags below
            } finally {
                try { extractor?.Release(); } catch { }
                try { extractor?.Dispose(); } catch { }
            }

            if (!item.HasNoAudioTrack &&
                (lowerName.Contains("_anull_video_", StringComparison.Ordinal) ||
                 lowerName.Contains("_anull_trickplay_", StringComparison.Ordinal))) {
                item.IsVideo = true;
                item.HasNoAudioTrack = true;
            }

            // Read native MP4/M4A/FLAC/ID3/RIFF tags (fixes missing track numbers, durations & albums on MP4 Atmos!)
            tagPath = directReadable
                ? item.FilePath
                : (effectiveUri != null && effectiveCtx != null ? AndroidFileHelper.TryGetDirectPath(effectiveCtx, effectiveUri) : null);
            if (!item.HasNoAudioTrack && CanOpenFileForRead(tagPath)) {
                ParsedAudioTags binTags = AudioTagReader.TryReadTags(tagPath);
                if (binTags != null) {
                    item.HasEmbeddedMusicTags = binTags.HasEmbeddedMusicTags;
                    if (item.DurationMs <= 0 && binTags.DurationMs > 0) item.DurationMs = binTags.DurationMs;
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

            bool canReadTagContainer = CanOpenFileForRead(tagPath) && AudioTagReader.IsContainerSupported(ext);
            bool needDurationFallback = item.DurationMs <= 0 && !item.HasNoAudioTrack;
            bool needTagFallback = !item.HasEmbeddedMusicTags && !item.HasNoAudioTrack && !extSpatial && !extSurround && !canReadTagContainer;
            if (needDurationFallback || needTagFallback) {
                ProbeWithMediaMetadataRetriever(effectiveCtx, item, effectiveUri, directReadable, fileName, needDurationFallback, needTagFallback);
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
            if (ext == ".atmos" || detectedMime.Contains("joc") || tagAtmos) {
                item.AudioBadge = "ATMOS";
            } else if (ext is ".thd" or ".mlp" || detectedMime.Contains("truehd") || lowerName.Contains("truehd")) {
                item.AudioBadge = "TRUEHD";
            } else if (ext is ".ec3" or ".eac3" || detectedMime.Contains("eac3")) {
                item.AudioBadge = "E-AC3";
            } else if (wavAdm || hasAdmToken) {
                item.AudioBadge = "ADM 3D";
            } else {
                item.AudioBadge = ch > 2 ? $"{ch}CH 3D" : AndroidStrings.Active["SpatialBadge"];
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

        ClassifyTrackRelevanceInternal(item, rules);
    }

    static void ProbeWithMediaMetadataRetriever(
        Context effectiveCtx,
        LibraryTrackItem item,
        Android.Net.Uri effectiveUri,
        bool directReadable,
        string fileName,
        bool probeDuration,
        bool probeTags) {
        MediaMetadataRetriever mmr = null;
        try {
            mmr = new MediaMetadataRetriever();
            if (directReadable) {
                mmr.SetDataSource(item.FilePath);
            } else if (effectiveUri != null && effectiveCtx != null) {
                mmr.SetDataSource(effectiveCtx, effectiveUri);
            } else {
                return;
            }

            if (probeDuration) {
                string durRaw = mmr.ExtractMetadata(MetadataKey.Duration);
                if (long.TryParse(durRaw, out long durMs) && durMs > 0) {
                    item.DurationMs = durMs;
                }
            }

            if (probeTags) {
                string rawTitle = CleanMediaStoreTag(mmr.ExtractMetadata(MetadataKey.Title));
                string rawArtist = CleanMediaStoreTag(mmr.ExtractMetadata(MetadataKey.Artist));
                string rawAlbumArtist = CleanMediaStoreTag(mmr.ExtractMetadata(MetadataKey.Albumartist));
                string rawAlbum = CleanMediaStoreTag(mmr.ExtractMetadata(MetadataKey.Album));
                TryParseSlashNumber(mmr.ExtractMetadata(MetadataKey.CdTrackNumber), out int trkNum);
                TryParseSlashNumber(mmr.ExtractMetadata(MetadataKey.DiscNumber), out int discNum);
                byte[] embeddedArt = mmr.GetEmbeddedPicture();

                bool hasRealAlbum = !string.IsNullOrWhiteSpace(rawAlbum)
                    && !IsGenericFolderName(rawAlbum)
                    && !string.Equals(rawAlbum, item.FolderName, StringComparison.OrdinalIgnoreCase);
                string fileStem = !string.IsNullOrEmpty(fileName) ? IOPath.GetFileNameWithoutExtension(fileName) : "";
                bool hasRealTitle = !string.IsNullOrWhiteSpace(rawTitle)
                    && !string.Equals(rawTitle, fileStem, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(rawTitle, fileName, StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(rawArtist) ||
                    !string.IsNullOrWhiteSpace(rawAlbumArtist) ||
                    hasRealAlbum ||
                    trkNum > 0 ||
                    discNum > 0 ||
                    hasRealTitle ||
                    (embeddedArt != null && embeddedArt.Length > 0)) {
                    item.HasEmbeddedMusicTags = true;
                }

                if (trkNum > 0 && item.TrackNumber <= 0) item.TrackNumber = trkNum;
                if (discNum > 0 && item.DiscNumber <= 0) item.DiscNumber = discNum;
                if (!string.IsNullOrWhiteSpace(rawAlbumArtist) && string.IsNullOrWhiteSpace(item.AlbumArtist)) item.AlbumArtist = rawAlbumArtist;
                if (!string.IsNullOrWhiteSpace(rawArtist) && string.IsNullOrWhiteSpace(item.Artist)) item.Artist = rawArtist;
                if (hasRealAlbum && string.IsNullOrWhiteSpace(item.Album)) item.Album = rawAlbum;
                if (hasRealTitle) item.Title = rawTitle;
            }
        } catch {
            // Best-effort metadata/duration probe
        } finally {
            try { mmr?.Release(); } catch { }
            try { mmr?.Dispose(); } catch { }
        }
    }

    static void InspectWavHeader(Context ctx, LibraryTrackItem item, out int channels, out bool isAdmBwf, out long wavDurationMs) {
        channels = 0;
        isAdmBwf = false;
        wavDurationMs = 0;
        try {
            using System.IO.Stream fs = OpenTrackStream(ctx, item);
            if (fs == null) return;
            byte[] buf = new byte[65536];
            int read = ReadUpTo(fs, buf, buf.Length);
            if (read < 44) return;
            if (buf[0] != 'R' || buf[1] != 'I' || buf[2] != 'F' || buf[3] != 'F') return;
            int offset = 12;
            uint byteRate = 0;
            uint dataBytes = 0;
            while (offset + 8 <= read) {
                string chunkId = System.Text.Encoding.ASCII.GetString(buf, offset, 4);
                int chunkSize = BitConverter.ToInt32(buf, offset + 4);
                if (chunkId == "fmt " && offset + 12 <= read) {
                    channels = BitConverter.ToUInt16(buf, offset + 10);
                    if (offset + 20 <= read) {
                        byteRate = BitConverter.ToUInt32(buf, offset + 16);
                    }
                } else if (chunkId == "data") {
                    dataBytes = BitConverter.ToUInt32(buf, offset + 4);
                } else if (chunkId is "axml" or "chna" or "bext") {
                    if (chunkId is "axml" or "chna") isAdmBwf = true;
                }
                if (chunkSize < 0) break;
                offset += 8 + chunkSize + (chunkSize & 1);
            }
            if (byteRate > 0) {
                long effectiveBytes = dataBytes > 0 ? dataBytes : Math.Max(0, item.FileSizeBytes - 44);
                if (effectiveBytes > 0) {
                    wavDurationMs = effectiveBytes * 1000L / byteRate;
                }
            }
        } catch {
            // Ignore malformed header
        }
    }

    static ICursor TryQueryMediaCursor(ContentResolver cr, Android.Net.Uri baseUri, string[] preferredCols, string[] fallbackCols) {
        try {
            return cr.Query(baseUri, preferredCols, null, null, null);
        } catch {
            return cr.Query(baseUri, fallbackCols, null, null, null);
        }
    }

    static string CleanMediaStoreTag(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();
        return "<unknown>".Equals(trimmed, StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
    }

    static bool TryParseSlashNumber(string raw, out int value) {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string part = raw.Split('/')[0].Trim();
        return int.TryParse(part, out value) && value > 0;
    }

    static void QueryMediaStore(Context ctx, bool isVideo, Dictionary<string, LibraryTrackItem> byPath) {
        if (ctx?.ContentResolver == null) return;
        try {
            Android.Net.Uri baseUri = isVideo
                ? MediaStore.Video.Media.ExternalContentUri
                : MediaStore.Audio.Media.ExternalContentUri;
            List<string> fallbackCols = isVideo
                ? ["_id", "title", "_display_name", "duration", "_data", IOpenableColumns.Size, "date_modified"]
                : ["_id", "title", "artist", "album", "album_id", "duration", "track", "_data", "_display_name", IOpenableColumns.Size, "date_modified"];
            List<string> preferredCols = isVideo
                ? ["_id", "title", "artist", "album", "duration", "_data", "_display_name", IOpenableColumns.Size, "date_modified"]
                : [.. fallbackCols];
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
                fallbackCols.Add(MediaStore.IMediaColumns.RelativePath);
                preferredCols.Add(MediaStore.IMediaColumns.RelativePath);
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
                preferredCols.Add("album_artist");
                if (isVideo) {
                    preferredCols.Add("cd_track_number");
                    preferredCols.Add("disc_number");
                }
            }

            using ICursor cursor = TryQueryMediaCursor(ctx.ContentResolver, baseUri, [.. preferredCols], [.. fallbackCols]);
            if (cursor == null) return;

            int idCol = cursor.GetColumnIndex("_id");
            int titleCol = cursor.GetColumnIndex("title");
            int artistCol = cursor.GetColumnIndex("artist");
            int albumArtistCol = cursor.GetColumnIndex("album_artist");
            int albumCol = cursor.GetColumnIndex("album");
            int albumIdCol = cursor.GetColumnIndex("album_id");
            int durCol = cursor.GetColumnIndex("duration");
            int trkCol = cursor.GetColumnIndex("track");
            int cdTrkCol = cursor.GetColumnIndex("cd_track_number");
            int discCol = cursor.GetColumnIndex("disc_number");
            int dataCol = cursor.GetColumnIndex("_data");
            int dispCol = cursor.GetColumnIndex("_display_name");
            int sizeCol = cursor.GetColumnIndex(IOpenableColumns.Size);
            int dateModCol = cursor.GetColumnIndex("date_modified");
            int relCol = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath)
                : -1;

            bool hasAllAccess = AndroidFileHelper.HasAllFilesAccess();
            string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/') ?? "/storage/emulated/0";

            while (cursor.MoveToNext()) {
                string rawPath = dataCol >= 0 ? (cursor.GetString(dataCol) ?? "") : "";
                if (!string.IsNullOrEmpty(rawPath) && IsIgnoredSystemPath(rawPath)) continue;

                long dur = durCol >= 0 ? cursor.GetLong(durCol) : 0;
                if (!isVideo && dur > 0 && dur < 4500) continue;

                long id = idCol >= 0 ? cursor.GetLong(idCol) : -1;
                Android.Net.Uri itemUri = id >= 0 ? ContentUris.WithAppendedId(baseUri, id) : null;
                string disp = dispCol >= 0 ? (cursor.GetString(dispCol) ?? "") : IOPath.GetFileName(rawPath);
                string relPath = relCol >= 0 ? (cursor.GetString(relCol) ?? "").Trim('/') : "";

                bool directReadable = !string.IsNullOrEmpty(rawPath) &&
                    (hasAllAccess ? IOFile.Exists(rawPath) : CanOpenFileForRead(rawPath));
                string trackKey = directReadable ? rawPath : (itemUri?.ToString() ?? "");
                if (string.IsNullOrEmpty(trackKey)) continue;

                string dir = directReadable
                    ? (IOPath.GetDirectoryName(rawPath) ?? extRoot)
                    : (!string.IsNullOrEmpty(rawPath)
                        ? (IOPath.GetDirectoryName(rawPath) ?? extRoot)
                        : (!string.IsNullOrEmpty(relPath) ? IOPath.Combine(extRoot, relPath) : extRoot));
                if (IsIgnoredSystemPath(dir + "/")) continue;
                string folderName = IOPath.GetFileName(dir) ?? "";

                string title = titleCol >= 0 ? (cursor.GetString(titleCol) ?? "") : "";
                if (string.IsNullOrWhiteSpace(title)) {
                    string nameSeed = !string.IsNullOrEmpty(disp) ? disp : rawPath;
                    title = IOPath.GetFileNameWithoutExtension(nameSeed);
                }

                string artist = artistCol >= 0 ? CleanMediaStoreTag(cursor.GetString(artistCol)) : "";
                string albumArtist = albumArtistCol >= 0 ? CleanMediaStoreTag(cursor.GetString(albumArtistCol)) : "";

                long albId = albumIdCol >= 0 ? cursor.GetLong(albumIdCol) : -1;
                int trkRaw = trkCol >= 0 ? cursor.GetInt(trkCol) : 0;
                int trkDisc = trkRaw > 1000 ? trkRaw / 1000 : 0;
                int trkNum = trkRaw > 1000 ? trkRaw % 1000 : trkRaw;
                if (trkNum <= 0 && cdTrkCol >= 0 && TryParseSlashNumber(cursor.GetString(cdTrkCol), out int cdTrk)) {
                    trkNum = cdTrk;
                }
                if (trkDisc <= 0 && discCol >= 0 && TryParseSlashNumber(cursor.GetString(discCol), out int parsedDisc)) {
                    trkDisc = parsedDisc;
                }

                string album = albumCol >= 0 ? CleanMediaStoreTag(cursor.GetString(albumCol)) : "";
                if (IsGenericFolderName(album) ||
                    (isVideo && string.Equals(album, folderName, StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(albumArtist) && trkNum <= 0)) {
                    album = "";
                }

                string nameForExt = !string.IsNullOrEmpty(disp) ? disp : rawPath;
                string ext = IOPath.GetExtension(nameForExt).TrimStart('.').ToUpperInvariant();
                long sizeBytes = sizeCol >= 0 ? cursor.GetLong(sizeCol) : 0;
                long modSec = dateModCol >= 0 ? cursor.GetLong(dateModCol) : 0;
                long modTicks = modSec > 0 ? DateTimeOffset.FromUnixTimeSeconds(modSec).UtcTicks : 0;

                if (isVideo && byPath.TryGetValue(trackKey, out LibraryTrackItem existing)) {
                    if (existing.DurationMs <= 0 && dur > 0) existing.DurationMs = dur;
                    if (existing.FileSizeBytes <= 0 && sizeBytes > 0) existing.FileSizeBytes = sizeBytes;
                    if (existing.LastModifiedTicks <= 0 && modTicks > 0) existing.LastModifiedTicks = modTicks;
                    if (string.IsNullOrWhiteSpace(existing.Artist) && !string.IsNullOrWhiteSpace(artist)) existing.Artist = artist;
                    if (string.IsNullOrWhiteSpace(existing.AlbumArtist) && !string.IsNullOrWhiteSpace(albumArtist)) existing.AlbumArtist = albumArtist;
                    if (string.IsNullOrWhiteSpace(existing.Album) && !string.IsNullOrWhiteSpace(album)) existing.Album = album;
                    if (existing.TrackNumber <= 0 && trkNum > 0) existing.TrackNumber = trkNum;
                    if (existing.DiscNumber <= 0 && trkDisc > 0) existing.DiscNumber = trkDisc;
                    continue;
                }

                byPath[trackKey] = new LibraryTrackItem {
                    FilePath = trackKey,
                    ContentUri = itemUri,
                    Title = title.Trim(),
                    Artist = artist,
                    AlbumArtist = albumArtist,
                    Album = album,
                    FolderPath = dir,
                    FolderName = folderName,
                    FormatBadge = string.IsNullOrEmpty(ext) ? (isVideo ? "VIDEO" : "AUDIO") : ext,
                    DurationMs = dur,
                    AlbumId = albId,
                    TrackNumber = trkNum,
                    DiscNumber = trkDisc,
                    IsVideo = isVideo,
                    FileSizeBytes = sizeBytes,
                    LastModifiedTicks = modTicks
                };
            }
        } catch (Exception ex) {
            AppLogger.Error($"MediaLibraryService.QueryMediaStore(video={isVideo})", ex);
        }
    }

    static void QueryDownloadsMediaStore(Context ctx, Dictionary<string, LibraryTrackItem> byPath) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) || ctx?.ContentResolver == null) return;
        try {
            Android.Net.Uri baseUri = MediaStore.Downloads.ExternalContentUri;
            if (baseUri == null) return;
            string[] cols = [
                "_id",
                MediaStore.IMediaColumns.DisplayName,
                MediaStore.IMediaColumns.Duration,
                IOpenableColumns.Size,
                "date_modified",
                "_data",
                MediaStore.IMediaColumns.RelativePath
            ];
            using ICursor cursor = ctx.ContentResolver.Query(baseUri, cols, null, null, null);
            if (cursor == null) return;

            int idCol = cursor.GetColumnIndex("_id");
            int dispCol = cursor.GetColumnIndex(MediaStore.IMediaColumns.DisplayName);
            int durCol = cursor.GetColumnIndex(MediaStore.IMediaColumns.Duration);
            int sizeCol = cursor.GetColumnIndex(IOpenableColumns.Size);
            int dateModCol = cursor.GetColumnIndex("date_modified");
            int dataCol = cursor.GetColumnIndex("_data");
            int relCol = cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath);

            bool hasAllAccess = AndroidFileHelper.HasAllFilesAccess();
            string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/') ?? "/storage/emulated/0";

            while (cursor.MoveToNext()) {
                string disp = dispCol >= 0 ? (cursor.GetString(dispCol) ?? "") : "";
                if (string.IsNullOrWhiteSpace(disp) || disp.StartsWith('.')) continue;

                string ext = IOPath.GetExtension(disp);
                bool isAudio = SupportedAudioExts.Contains(ext);
                bool isVideo = SupportedVideoExts.Contains(ext);
                if (!isAudio && !isVideo) continue;

                string rawPath = dataCol >= 0 ? (cursor.GetString(dataCol) ?? "") : "";
                if (!string.IsNullOrEmpty(rawPath) && (IsIgnoredSystemPath(rawPath) || byPath.ContainsKey(rawPath))) continue;

                long id = idCol >= 0 ? cursor.GetLong(idCol) : -1;
                Android.Net.Uri itemUri = id >= 0 ? ContentUris.WithAppendedId(baseUri, id) : null;
                bool directReadable = !string.IsNullOrEmpty(rawPath) &&
                    (hasAllAccess ? IOFile.Exists(rawPath) : CanOpenFileForRead(rawPath));
                string trackKey = directReadable ? rawPath : (itemUri?.ToString() ?? "");
                if (string.IsNullOrEmpty(trackKey) || byPath.ContainsKey(trackKey)) continue;

                string relPath = relCol >= 0 ? (cursor.GetString(relCol) ?? "").Trim('/') : "";
                string dir = directReadable
                    ? (IOPath.GetDirectoryName(rawPath) ?? extRoot)
                    : (!string.IsNullOrEmpty(rawPath)
                        ? (IOPath.GetDirectoryName(rawPath) ?? extRoot)
                        : (!string.IsNullOrEmpty(relPath) ? IOPath.Combine(extRoot, relPath) : IOPath.Combine(extRoot, "Download")));
                if (IsIgnoredSystemPath(dir + "/")) continue;

                string baseName = IOPath.GetFileNameWithoutExtension(disp);
                string folderName = IOPath.GetFileName(dir) ?? "";
                ParseHeuristicNames(baseName, folderName, out string artist, out string title, out string album, out int discNum, out int trkNum);

                long durMs = durCol >= 0 ? Math.Max(0, cursor.GetLong(durCol)) : 0;
                long sizeBytes = sizeCol >= 0 ? cursor.GetLong(sizeCol) : 0;
                long modSec = dateModCol >= 0 ? cursor.GetLong(dateModCol) : 0;
                long modTicks = modSec > 0 ? DateTimeOffset.FromUnixTimeSeconds(modSec).UtcTicks : 0;

                byPath[trackKey] = new LibraryTrackItem {
                    FilePath = trackKey,
                    ContentUri = itemUri,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    FolderPath = dir,
                    FolderName = folderName,
                    FormatBadge = ext.TrimStart('.').ToUpperInvariant(),
                    DurationMs = durMs,
                    DiscNumber = discNum,
                    TrackNumber = trkNum,
                    IsVideo = isVideo,
                    FileSizeBytes = sizeBytes,
                    LastModifiedTicks = modTicks
                };
            }
        } catch (Exception ex) {
            AppLogger.Error("MediaLibraryService.QueryDownloadsMediaStore", ex);
        }
    }

    static void ScanCommonMediaFolders(Context ctx, Dictionary<string, LibraryTrackItem> byPath) {
        if (ctx != null && !AndroidFileHelper.HasMediaReadPermission(ctx)) {
            return;
        }
        bool hasAllAccess = AndroidFileHelper.HasAllFilesAccess();
        string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        string[] roots = [
            IOPath.Combine(extRoot, "Music"),
            IOPath.Combine(extRoot, "Download"),
            IOPath.Combine(extRoot, "Movies"),
            IOPath.Combine(extRoot, "Audiobooks")
        ];

        foreach (string root in roots) {
            try {
                if (!Directory.Exists(root)) continue;
                WalkFolder(root, depth: 0, maxDepth: 5, byPath, hasAllAccess);
            } catch {
                // Scoped storage may block direct POSIX root enumeration
            }
        }
    }

    static void WalkFolder(string dir, int depth, int maxDepth, Dictionary<string, LibraryTrackItem> byPath, bool hasAllAccess) {
        if (depth > maxDepth || IsIgnoredSystemPath(dir)) return;
        try {
            foreach (string file in Directory.EnumerateFiles(dir)) {
                string ext = IOPath.GetExtension(file);
                if (string.IsNullOrEmpty(ext)) continue;
                bool isAudio = SupportedAudioExts.Contains(ext);
                bool isVideo = SupportedVideoExts.Contains(ext);
                if (!isAudio && !isVideo) continue;
                if (byPath.ContainsKey(file) || IsIgnoredSystemPath(file)) continue;
                if (!hasAllAccess && !CanOpenFileForRead(file)) continue;

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
                WalkFolder(sub, depth + 1, maxDepth, byPath, hasAllAccess);
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
        lock (thumbLock) {
            if (thumbCache.TryGetValue(cacheKey, out Bitmap bmp) && bmp != null) {
                try {
                    if (bmp.Handle != IntPtr.Zero && !bmp.IsRecycled) {
                        thumbLru.Remove(cacheKey);
                        thumbLru.AddLast(cacheKey);
                        return bmp;
                    }
                } catch {
                    // Evict stale reference below
                }
                thumbCache.TryRemove(cacheKey, out _);
                thumbLru.Remove(cacheKey);
            }
        }
        return null;
    }

    public static Task<Bitmap> LoadThumbnailAsync(Context ctx, LibraryTrackItem item, int sizePx = 240) {
        if (item == null) return Task.FromResult<Bitmap>(null);
        string key = item.CacheKey;
        Bitmap cached = TryGetCachedThumbnail(key);
        if (cached != null) return Task.FromResult(cached);

        Context effectiveCtx = (ctx?.ApplicationContext ?? ctx) ?? Android.App.Application.Context;
        return inflightThumbs.GetOrAdd(key, _ => LoadThumbnailCoreAsync(effectiveCtx, item, key, sizePx));
    }

    static async Task<Bitmap> LoadThumbnailCoreAsync(Context effectiveCtx, LibraryTrackItem item, string key, int sizePx) {
        await thumbSemaphore.WaitAsync().ConfigureAwait(false);
        try {
            Bitmap cached = TryGetCachedThumbnail(key);
            if (cached != null) return cached;

            Bitmap loaded = null;
            Android.Net.Uri effectiveUri = item.ContentUri ?? TryParseUri(item.FilePath);
            try {
                // 0. Android Q+ MediaStore LoadThumbnail FIRST for video files to avoid spawning heavy native codec frames
                if (item.IsVideo && effectiveUri != null && OperatingSystem.IsAndroidVersionAtLeast(29) && effectiveCtx?.ContentResolver != null) {
                    try {
                        loaded = effectiveCtx.ContentResolver.LoadThumbnail(effectiveUri, new Android.Util.Size(sizePx, sizePx), null);
                    } catch {
                        // Thumbnail may not exist in MediaStore yet
                    }
                }

                // 1. Embedded artwork inside the exact media file FIRST (prevents cross-song / folder cover mix-ups)
                if (loaded == null) {
                    bool directReadable = CanOpenFileForRead(item.FilePath);
                    if (directReadable || (effectiveUri != null && effectiveCtx != null)) {
                        MediaMetadataRetriever mmr = null;
                        try {
                            mmr = new MediaMetadataRetriever();
                            if (directReadable) {
                                mmr.SetDataSource(item.FilePath);
                            } else {
                                mmr.SetDataSource(effectiveCtx, effectiveUri);
                            }
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
                        } finally {
                            try { mmr?.Release(); } catch { }
                            try { mmr?.Dispose(); } catch { }
                        }
                    }
                }

                // 2. Online-enriched persistent cover cache (keyed safely)
                if (loaded == null && effectiveCtx != null) {
                    string pathKey = !string.IsNullOrEmpty(item.FilePath) ? item.FilePath : (effectiveUri?.ToString() ?? "");
                    string cachedCoverPath = OnlineMetadataService.GetCachedCoverPath(effectiveCtx, item.Title, item.Artist, item.Album, pathKey);
                    if (!string.IsNullOrEmpty(cachedCoverPath) && IOFile.Exists(cachedCoverPath)) {
                        loaded = DecodeSampledFile(cachedCoverPath, sizePx);
                    }
                }

                // 3. Dedicated folder artwork ONLY in real album subfolders (never root Download/Music/Movies!)
                if (loaded == null && !IsGenericRootFolderPath(item.FolderPath)) {
                    try {
                        if (Directory.Exists(item.FolderPath)) {
                            foreach (string name in ExternalCoverNames) {
                                string fp = IOPath.Combine(item.FolderPath, name);
                                if (CanOpenFileForRead(fp)) {
                                    loaded = DecodeSampledFile(fp, sizePx);
                                    if (loaded != null) break;
                                }
                            }
                        }
                    } catch {
                        // Scoped storage may block folder artwork enumeration
                    }
                }

                // 4. Android Q+ MediaStore LoadThumbnail ONLY when the item has a real album (or audio)
                if (loaded == null && !item.IsVideo && effectiveUri != null && OperatingSystem.IsAndroidVersionAtLeast(29) && effectiveCtx?.ContentResolver != null
                    && !IsGenericFolderName(item.Album)) {
                    try {
                        loaded = effectiveCtx.ContentResolver.LoadThumbnail(effectiveUri, new Android.Util.Size(sizePx, sizePx), null);
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
        } finally {
            thumbSemaphore.Release();
            inflightThumbs.TryRemove(key, out _);
        }
    }

    static bool IsUriString(string value) =>
        !string.IsNullOrEmpty(value) &&
        (value.StartsWith("content://", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    static Android.Net.Uri TryParseUri(string pathOrUri) {
        if (IsUriString(pathOrUri)) {
            try {
                return Android.Net.Uri.Parse(pathOrUri);
            } catch {
                return null;
            }
        }
        return null;
    }

    static bool CanOpenFileForRead(string path) {
        if (string.IsNullOrEmpty(path) || IsUriString(path)) {
            return false;
        }
        try {
            if (!IOFile.Exists(path)) {
                return false;
            }
            using FileStream fs = IOFile.OpenRead(path);
            return fs.CanRead;
        } catch {
            return false;
        }
    }

    static string GetTrackStemName(LibraryTrackItem item) {
        if (item == null) return "";
        if (!string.IsNullOrEmpty(item.FilePath) && !IsUriString(item.FilePath)) {
            return IOPath.GetFileNameWithoutExtension(item.FilePath);
        }
        return item.Title ?? "";
    }

    static System.IO.Stream OpenTrackStream(Context ctx, LibraryTrackItem item) {
        if (item == null) return null;
        if (CanOpenFileForRead(item.FilePath)) {
            try {
                return IOFile.OpenRead(item.FilePath);
            } catch {
                // Fall back to ContentUri below
            }
        }
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);
        return uri != null && ctx != null ? AndroidFileHelper.OpenReadableStream(ctx, uri) : null;
    }

    static int ReadUpTo(System.IO.Stream stream, byte[] buffer, int maxBytes) {
        int total = 0;
        while (total < maxBytes) {
            int n = stream.Read(buffer, total, maxBytes - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
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
            thumbCache[key] = bmp;
            thumbLru.Remove(key);
            thumbLru.AddLast(key);
            while (thumbLru.Count > 96) {
                string oldest = thumbLru.First.Value;
                thumbLru.RemoveFirst();
                thumbCache.TryRemove(oldest, out _);
            }
        }
    }

    static Bitmap DecodeSampledFile(string path, int maxDim) =>
        MediaServiceUtils.DecodeScaledBitmapSafe(path, maxDim);

    static Bitmap DecodeSampledBytes(byte[] bytes, int maxDim) =>
        MediaServiceUtils.DecodeScaledBitmapSafe(bytes, maxDim);
}
