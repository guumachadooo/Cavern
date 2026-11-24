using Android;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Media;
using Android.OS;
using Android.Provider;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Represents a media entry discovered via <see cref="MediaStore"/> under Android Scoped Storage.
/// </summary>
public sealed class MediaStoreEntry {
    public Android.Net.Uri ContentUri { get; init; }
    public string DirectPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public long SizeBytes { get; init; }
}

/// <summary>
/// User-selectable public storage folder for Studio and Classic audio exports.
/// </summary>
public enum ExportStorageDestination {
    /// <summary>Public <c>Music/Cavernize</c> folder indexed by Android Music players (standard audio extensions).</summary>
    MusicCavernize = 0,
    /// <summary>Public <c>Download/Cavernize</c> folder (also used automatically for <c>.iamf</c>, <c>.wv</c>, and <c>.amb</c>).</summary>
    DownloadsCavernize = 1
}

/// <summary>
/// Result returned after publishing an exported audio file to visible public Android storage.
/// </summary>
public sealed class ExportPublishResult {
    public string PublicDisplayPath { get; init; } = "";
    public string FolderDisplayPath { get; init; } = "";
    public string PublicFolderAbsolutePath { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public Android.Net.Uri ContentUri { get; init; }
    public bool RoutedToDownloadsForContainerCompat { get; init; }
}

/// <summary>
/// Resolves Android SAF content URIs to readable file paths, queries MediaStore under Scoped Storage,
/// and publishes or shares exported files via <see cref="MediaStore"/> and <see cref="ApkFileProvider"/>.
/// </summary>
public static class AndroidFileHelper {
    /// <summary>
    /// Checks whether the app has direct filesystem read/write access to shared external storage.
    /// </summary>
    public static bool HasAllFilesAccess() {
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
            return Android.OS.Environment.IsExternalStorageManager;
        }
        return true;
    }

    /// <summary>
    /// Checks whether the app holds either All-Files access or scoped media read permissions.
    /// </summary>
    public static bool HasMediaReadPermission(Context context) {
        if (context == null) {
            return false;
        }
        if (HasAllFilesAccess()) {
            return true;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
            return context.CheckSelfPermission(Manifest.Permission.ReadMediaAudio) == Permission.Granted ||
                   context.CheckSelfPermission(Manifest.Permission.ReadMediaVideo) == Permission.Granted;
        }
        return context.CheckSelfPermission(Manifest.Permission.ReadExternalStorage) == Permission.Granted;
    }

    /// <summary>
    /// Opens a readable stream for either a <c>file://</c> or <c>content://</c> URI with typed asset fallback.
    /// </summary>
    public static System.IO.Stream OpenReadableStream(Context context, Android.Net.Uri uri) {
        if (context == null || uri == null) {
            return null;
        }
        if ("file".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)) {
            return !string.IsNullOrEmpty(uri.Path) && File.Exists(uri.Path) ? File.OpenRead(uri.Path) : null;
        }
        ContentResolver resolver = context.ContentResolver;
        if (resolver == null) {
            return null;
        }
        try {
            System.IO.Stream direct = resolver.OpenInputStream(uri);
            if (direct != null) {
                return direct;
            }
        } catch {
            // Try asset descriptor fallbacks below
        }
        try {
            var typed = resolver.OpenTypedAssetFileDescriptor(uri, "*/*", null);
            if (typed != null) {
                return typed.CreateInputStream();
            }
        } catch {
            // Try raw asset descriptor fallback below
        }
        try {
            var afd = resolver.OpenAssetFileDescriptor(uri, "r");
            if (afd != null) {
                return afd.CreateInputStream();
            }
        } catch {
            // Unreadable content URI
        }
        return null;
    }

    /// <summary>
    /// Resolves an Android <see cref="Android.Net.Uri"/> into a readable local file path.
    /// Attempts zero-copy direct path resolution first; falls back to copying into app cache.
    /// </summary>
    public static string ResolveReadablePath(Context context, Android.Net.Uri uri, Action<double, string> onProgress = null) {
        if (context == null || uri == null) {
            return null;
        }

        if ("file".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)) {
            return CanOpenForRead(uri.Path) ? uri.Path : null;
        }

        string direct = TryGetDirectPath(context, uri);
        if (!string.IsNullOrEmpty(direct) && CanOpenForRead(direct)) {
            return direct;
        }

        string displayName = Path.GetFileName(GetDisplayName(context, uri) ?? "") ?? "";
        if (string.IsNullOrWhiteSpace(displayName)) {
            displayName = $"cavern_input_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.bin";
        }
        foreach (char c in Path.GetInvalidFileNameChars()) {
            displayName = displayName.Replace(c, '_');
        }

        string cacheFolder = Path.Combine(context.CacheDir.AbsolutePath, "opened");
        Directory.CreateDirectory(cacheFolder);
        string destPath = Path.Combine(cacheFolder, displayName);
        string tempPath = destPath + ".part";

        long expectedSize = GetFileSize(context, uri);
        using System.IO.Stream src = OpenReadableStream(context, uri)
            ?? throw new IOException("Cannot open selected file stream.");

        try {
            using (FileStream dst = File.Create(tempPath)) {
                byte[] buffer = new byte[262144];
                long totalRead = 0;
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0) {
                    dst.Write(buffer, 0, read);
                    totalRead += read;
                    if (expectedSize > 0) {
                        onProgress?.Invoke(Math.Clamp(totalRead / (double)expectedSize, 0, 1), displayName);
                    }
                }
            }
            File.Move(tempPath, destPath, overwrite: true);
            return destPath;
        } catch {
            try {
                if (File.Exists(tempPath)) {
                    File.Delete(tempPath);
                }
            } catch {
                // Ignore cleanup error
            }
            throw;
        }
    }

    /// <summary>
    /// Gets the best directory for writing output files (direct Downloads/Cavernize when writable,
    /// else app-external <c>GetExternalFilesDir</c>, else internal <c>FilesDir</c>).
    /// </summary>
    public static string GetWorkingOutputDir(Context context, string subfolder = null) {
        string publicDownloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        if (!string.IsNullOrEmpty(publicDownloads)) {
            string candidate = string.IsNullOrEmpty(subfolder)
                ? Path.Combine(publicDownloads, "Cavernize")
                : Path.Combine(publicDownloads, "Cavernize", subfolder);

            if (CanWriteToDirectory(candidate)) {
                return candidate;
            }
        }

        string appDownloads = context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        if (!string.IsNullOrEmpty(appDownloads)) {
            string extFallback = string.IsNullOrEmpty(subfolder)
                ? Path.Combine(appDownloads, "Cavernize")
                : Path.Combine(appDownloads, "Cavernize", subfolder);
            if (CanWriteToDirectory(extFallback)) {
                return extFallback;
            }
        }

        string intRoot = context.FilesDir.AbsolutePath;
        string intFallback = string.IsNullOrEmpty(subfolder)
            ? Path.Combine(intRoot, "Cavernize")
            : Path.Combine(intRoot, "Cavernize", subfolder);
        Directory.CreateDirectory(intFallback);
        return intFallback;
    }

    /// <summary>
    /// Returns true when <paramref name="fileNameOrExt"/> is a standard Android audio format accepted by
    /// <see cref="MediaStore.Audio.Media"/> under <c>Music/Cavernize</c>. Specialized spatial containers
    /// (<c>.iamf</c>, <c>.wv</c>, <c>.amb</c>) route to <c>Download/Cavernize</c> (<see cref="MediaStore.Downloads"/>).
    /// </summary>
    public static bool IsStandardAndroidMusicExtension(string fileNameOrExt) {
        if (string.IsNullOrWhiteSpace(fileNameOrExt)) {
            return true;
        }
        string ext = fileNameOrExt.Contains('.')
            ? Path.GetExtension(fileNameOrExt).ToLowerInvariant()
            : "." + fileNameOrExt.Trim().ToLowerInvariant();
        return ext is ".flac" or ".wav" or ".opus" or ".m4a" or ".mp3" or ".eac3" or ".ec3" or ".ac3" or ".ogg";
    }

    /// <summary>
    /// Resolves the best writable staging or direct-public directory for a Studio/Classic export.
    /// Prefers direct writes to <c>Music/Cavernize</c> (standard audio) or <c>Download/Cavernize</c> (spatial containers or Downloads mode),
    /// falling back to ephemeral cache staging cleaned up automatically by <see cref="PublishExportedAudio"/>.
    /// </summary>
    public static string GetExportWorkingDir(
        Context context,
        ExportStorageDestination requestedDest,
        string extensionOrFileName,
        out ExportStorageDestination effectiveDest,
        out bool isDirectPublic) {
        bool standardAudio = IsStandardAndroidMusicExtension(extensionOrFileName);
        effectiveDest = requestedDest == ExportStorageDestination.MusicCavernize && standardAudio
            ? ExportStorageDestination.MusicCavernize
            : ExportStorageDestination.DownloadsCavernize;

        string publicBase = effectiveDest == ExportStorageDestination.MusicCavernize
            ? Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic)?.AbsolutePath
            : Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        if (!string.IsNullOrEmpty(publicBase)) {
            string directPublic = Path.Combine(publicBase, "Cavernize");
            if (CanWriteToDirectory(directPublic)) {
                isDirectPublic = true;
                return directPublic;
            }
        }

        // Ephemeral staging directory (deleted immediately after atomic MediaStore commit — zero permanent private duplicates)
        isDirectPublic = false;
        string cacheRoot = context?.CacheDir?.AbsolutePath ?? context?.FilesDir?.AbsolutePath ?? "/tmp";
        string stageDir = Path.Combine(cacheRoot, "export_stage");
        Directory.CreateDirectory(stageDir);
        return stageDir;
    }

    /// <summary>
    /// Returns the public absolute directory path (<c>/storage/emulated/0/Music/Cavernize</c> or <c>Download/Cavernize</c>)
    /// for folder browsing / <c>Locate</c> actions.
    /// </summary>
    public static string GetPublicExportFolderAbsolutePath(ExportStorageDestination dest) {
        string baseDir = dest == ExportStorageDestination.MusicCavernize
            ? Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic)?.AbsolutePath
            : Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        if (string.IsNullOrEmpty(baseDir)) {
            string root = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
            baseDir = Path.Combine(root, dest == ExportStorageDestination.MusicCavernize ? "Music" : "Download");
        }
        return Path.Combine(baseDir, "Cavernize");
    }

    /// <summary>
    /// Publishes an exported audio file to visible public storage (<c>Music/Cavernize</c> for standard audio,
    /// <c>Download/Cavernize</c> for <c>.iamf</c>/<c>.wv</c>/<c>.amb</c> or when Downloads is selected), indexes it via
    /// <see cref="MediaScannerConnection"/> / <see cref="MediaStore"/>, and deletes private staging copies after atomic commit.
    /// </summary>
    public static ExportPublishResult PublishExportedAudio(
        Context context,
        string localPath,
        ExportStorageDestination requestedDest) {
        if (context == null || string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) {
            return new ExportPublishResult { LocalPath = localPath ?? "" };
        }

        string fileName = Path.GetFileName(localPath);
        bool standardAudio = IsStandardAndroidMusicExtension(fileName);
        ExportStorageDestination effectiveDest = requestedDest == ExportStorageDestination.MusicCavernize && standardAudio
            ? ExportStorageDestination.MusicCavernize
            : ExportStorageDestination.DownloadsCavernize;
        bool routedForCompat = requestedDest == ExportStorageDestination.MusicCavernize &&
                               effectiveDest == ExportStorageDestination.DownloadsCavernize;

        string folderDisplay = effectiveDest == ExportStorageDestination.MusicCavernize
            ? "Music/Cavernize"
            : "Download/Cavernize";
        string publicFolderAbs = GetPublicExportFolderAbsolutePath(effectiveDest);
        string mime = GetMimeType(fileName);

        bool alreadyInTargetPublic = !string.IsNullOrEmpty(publicFolderAbs) &&
            localPath.StartsWith(publicFolderAbs, StringComparison.OrdinalIgnoreCase);

        if (alreadyInTargetPublic) {
            try {
                MediaScannerConnection.ScanFile(context, [localPath], [mime], null);
            } catch {
                // Best-effort scanner notification
            }
            Android.Net.Uri existingUri = effectiveDest == ExportStorageDestination.MusicCavernize
                ? TryFindMediaStoreAudioUri(context, localPath)
                : TryFindMediaStoreDownloadUri(context, localPath);
            return new ExportPublishResult {
                PublicDisplayPath = $"{folderDisplay}/{fileName}",
                FolderDisplayPath = folderDisplay,
                PublicFolderAbsolutePath = publicFolderAbs,
                LocalPath = localPath,
                ContentUri = existingUri ?? ApkFileProvider.GetUriForFile(context, localPath),
                RoutedToDownloadsForContainerCompat = routedForCompat
            };
        }

        // Atomic MediaStore commit (API 29+) into Music/Cavernize/ or Download/Cavernize/
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
            Android.Net.Uri collectionUri = effectiveDest == ExportStorageDestination.MusicCavernize
                ? MediaStore.Audio.Media.ExternalContentUri
                : MediaStore.Downloads.ExternalContentUri;
            Android.Net.Uri itemUri = null;
            ContentResolver resolver = context.ContentResolver;
            try {
                ContentValues values = new();
                values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
                values.Put(MediaStore.IMediaColumns.MimeType, mime);
                values.Put(MediaStore.IMediaColumns.RelativePath, folderDisplay + "/");
                values.Put(MediaStore.IMediaColumns.IsPending, 1);

                itemUri = resolver?.Insert(collectionUri, values);
                if (itemUri != null) {
                    using (System.IO.Stream outStream = resolver.OpenOutputStream(itemUri)
                        ?? throw new IOException("Cannot open MediaStore output stream."))
                    using (FileStream inStream = File.OpenRead(localPath)) {
                        inStream.CopyTo(outStream);
                    }
                    values.Clear();
                    values.Put(MediaStore.IMediaColumns.IsPending, 0);
                    resolver.Update(itemUri, values, null, null);

                    string publishedDirectPath = TryGetDirectPath(context, itemUri) ?? Path.Combine(publicFolderAbs, fileName);
                    // Delete ephemeral staging copy ONLY after MediaStore atomic commit succeeds (0 duplicate storage!)
                    string cacheRoot = context.CacheDir?.AbsolutePath ?? "";
                    bool isStagingFile = !string.IsNullOrEmpty(cacheRoot) &&
                        localPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase);
                    if (isStagingFile) {
                        try { File.Delete(localPath); } catch { }
                    }
                    return new ExportPublishResult {
                        PublicDisplayPath = $"{folderDisplay}/{fileName}",
                        FolderDisplayPath = folderDisplay,
                        PublicFolderAbsolutePath = publicFolderAbs,
                        LocalPath = File.Exists(publishedDirectPath) ? publishedDirectPath : (File.Exists(localPath) ? localPath : ""),
                        ContentUri = itemUri,
                        RoutedToDownloadsForContainerCompat = routedForCompat
                    };
                }
            } catch (Exception ex) {
                if (itemUri != null && resolver != null) {
                    try { resolver.Delete(itemUri, null, null); } catch { }
                }
                AppLogger.Warn($"PublishExportedAudio MediaStore fallback ({folderDisplay}): {ex.Message}");
            }
        }

        // Legacy fallback: retain file and expose shareable FileProvider URI
        return new ExportPublishResult {
            PublicDisplayPath = localPath,
            FolderDisplayPath = Path.GetDirectoryName(localPath) ?? folderDisplay,
            PublicFolderAbsolutePath = Path.GetDirectoryName(localPath) ?? publicFolderAbs,
            LocalPath = localPath,
            ContentUri = ApkFileProvider.GetUriForFile(context, localPath),
            RoutedToDownloadsForContainerCompat = routedForCompat
        };
    }

    /// <summary>
    /// Ensures a file written to <paramref name="localPath"/> is visible in the public Downloads/Cavernize folder,
    /// returning a MediaStore or <see cref="ApkFileProvider"/> <c>content://</c> URI for sharing.
    /// </summary>
    public static Android.Net.Uri PublishToDownloads(Context context, string localPath, string relativeSubfolder = null) {
        if (string.IsNullOrEmpty(relativeSubfolder)) {
            return PublishExportedAudio(context, localPath, ExportStorageDestination.DownloadsCavernize).ContentUri;
        }
        if (context == null || string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) {
            return null;
        }

        string publicDownloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath ?? "";
        bool alreadyInPublicDownloads = !string.IsNullOrEmpty(publicDownloads) &&
            localPath.StartsWith(publicDownloads, StringComparison.OrdinalIgnoreCase);

        if (alreadyInPublicDownloads) {
            MediaScannerConnection.ScanFile(context, [localPath], [GetMimeType(localPath)], null);
            return TryFindMediaStoreDownloadUri(context, localPath)
                ?? ApkFileProvider.GetUriForFile(context, localPath);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
            Android.Net.Uri itemUri = null;
            ContentResolver resolver = context.ContentResolver;
            try {
                string fileName = Path.GetFileName(localPath);
                string relPath = $"Download/Cavernize/{relativeSubfolder.Trim('/')}/";

                ContentValues values = new();
                values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
                values.Put(MediaStore.IMediaColumns.MimeType, GetMimeType(fileName));
                values.Put(MediaStore.IMediaColumns.RelativePath, relPath);
                values.Put(MediaStore.IMediaColumns.IsPending, 1);

                itemUri = resolver?.Insert(MediaStore.Downloads.ExternalContentUri, values);
                if (itemUri != null) {
                    using (System.IO.Stream outStream = resolver.OpenOutputStream(itemUri)
                        ?? throw new IOException("Cannot open MediaStore output stream."))
                    using (FileStream inStream = File.OpenRead(localPath)) {
                        inStream.CopyTo(outStream);
                    }
                    values.Clear();
                    values.Put(MediaStore.IMediaColumns.IsPending, 0);
                    resolver.Update(itemUri, values, null, null);
                    return itemUri;
                }
            } catch (Exception ex) {
                if (itemUri != null && resolver != null) {
                    try {
                        resolver.Delete(itemUri, null, null);
                    } catch {
                        // Ignore cleanup error
                    }
                }
                AppLogger.Warn($"PublishToDownloads MediaStore fallback: {ex.Message}");
            }
        }

        return ApkFileProvider.GetUriForFile(context, localPath);
    }

    /// <summary>
    /// Returns a shareable <c>content://</c> <see cref="Android.Net.Uri"/> backed by <see cref="ApkFileProvider"/>
    /// for <paramref name="localPath"/> across all Android API levels (26–36).
    /// </summary>
    public static Android.Net.Uri GetShareableUri(Context context, string localPath) {
        if (context == null || string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) {
            return null;
        }
        return ApkFileProvider.GetUriForFile(context, localPath);
    }

    /// <summary>
    /// Queries <see cref="MediaStore"/> audio, video, and download collections safely under Scoped Storage
    /// when direct POSIX directory enumeration is unavailable.
    /// </summary>
    public static List<MediaStoreEntry> QueryMediaStoreEntries(
        Context context,
        HashSet<string> allowedExtensions,
        bool allowAllFiles,
        int maxResults = 300) {
        List<MediaStoreEntry> results = [];
        if (context?.ContentResolver == null) {
            return results;
        }

        List<Android.Net.Uri> collections = [
            MediaStore.Audio.Media.ExternalContentUri,
            MediaStore.Video.Media.ExternalContentUri
        ];
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
            collections.Add(MediaStore.Downloads.ExternalContentUri);
        }

        string extRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath?.TrimEnd('/') ?? "/storage/emulated/0";
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Android.Net.Uri collectionUri in collections) {
            if (collectionUri == null || results.Count >= maxResults) {
                continue;
            }
            try {
                List<string> cols = [
                    "_id",
                    MediaStore.IMediaColumns.DisplayName,
                    IOpenableColumns.Size,
                    "_data"
                ];
                if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
                    cols.Add(MediaStore.IMediaColumns.RelativePath);
                }

                using ICursor cursor = context.ContentResolver.Query(
                    collectionUri,
                    [.. cols],
                    null,
                    null,
                    $"{MediaStore.IMediaColumns.DisplayName} ASC");
                if (cursor == null) {
                    continue;
                }

                int idCol = cursor.GetColumnIndex("_id");
                int nameCol = cursor.GetColumnIndex(MediaStore.IMediaColumns.DisplayName);
                int sizeCol = cursor.GetColumnIndex(IOpenableColumns.Size);
                int dataCol = cursor.GetColumnIndex("_data");
                int relCol = OperatingSystem.IsAndroidVersionAtLeast(29)
                    ? cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath)
                    : -1;

                while (cursor.MoveToNext() && results.Count < maxResults) {
                    string name = nameCol >= 0 ? cursor.GetString(nameCol) : null;
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) {
                        continue;
                    }
                    string ext = Path.GetExtension(name);
                    if (!allowAllFiles) {
                        bool matches = allowedExtensions is { Count: > 0 }
                            ? (!string.IsNullOrEmpty(ext) && allowedExtensions.Contains(ext))
                            : MediaLibraryService.IsSupportedMediaExtension(ext);
                        if (!matches) {
                            continue;
                        }
                    }

                    long id = idCol >= 0 ? cursor.GetLong(idCol) : -1;
                    Android.Net.Uri itemUri = id >= 0 ? ContentUris.WithAppendedId(collectionUri, id) : null;
                    string rawData = dataCol >= 0 ? cursor.GetString(dataCol) : null;
                    string directPath = !string.IsNullOrEmpty(rawData) && CanOpenForRead(rawData) ? rawData : "";
                    string relPath = relCol >= 0 ? (cursor.GetString(relCol) ?? "").Trim('/') : "";

                    string folderPath = !string.IsNullOrEmpty(directPath)
                        ? (Path.GetDirectoryName(directPath) ?? extRoot)
                        : (!string.IsNullOrEmpty(relPath) ? Path.Combine(extRoot, relPath) : extRoot);

                    string dedupeKey = !string.IsNullOrEmpty(directPath)
                        ? directPath
                        : (itemUri?.ToString() ?? $"{folderPath}/{name}");
                    if (!seen.Add(dedupeKey)) {
                        continue;
                    }

                    long size = sizeCol >= 0 ? cursor.GetLong(sizeCol) : 0;
                    results.Add(new MediaStoreEntry {
                        ContentUri = itemUri,
                        DirectPath = directPath,
                        DisplayName = name,
                        FolderPath = folderPath.TrimEnd('/'),
                        SizeBytes = size
                    });
                }
            } catch {
                // Skip restricted or unsupported MediaStore collection
            }
        }
        return results;
    }

    static Android.Net.Uri TryFindMediaStoreAudioUri(Context context, string localPath) {
        if (context?.ContentResolver == null || string.IsNullOrEmpty(localPath)) {
            return null;
        }
        try {
            using ICursor cursor = context.ContentResolver.Query(
                MediaStore.Audio.Media.ExternalContentUri,
                ["_id"],
                "_data = ?",
                [localPath],
                null);
            if (cursor != null && cursor.MoveToFirst()) {
                int idIdx = cursor.GetColumnIndex("_id");
                if (idIdx >= 0) {
                    long id = cursor.GetLong(idIdx);
                    if (id >= 0) {
                        return ContentUris.WithAppendedId(MediaStore.Audio.Media.ExternalContentUri, id);
                    }
                }
            }
        } catch {
            // Fall back to FileProvider
        }
        return null;
    }

    static Android.Net.Uri TryFindMediaStoreDownloadUri(Context context, string localPath) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) || context?.ContentResolver == null || string.IsNullOrEmpty(localPath)) {
            return null;
        }
        try {
            using ICursor cursor = context.ContentResolver.Query(
                MediaStore.Downloads.ExternalContentUri,
                ["_id"],
                "_data = ?",
                [localPath],
                null);
            if (cursor != null && cursor.MoveToFirst()) {
                int idIdx = cursor.GetColumnIndex("_id");
                if (idIdx >= 0) {
                    long id = cursor.GetLong(idIdx);
                    if (id >= 0) {
                        return ContentUris.WithAppendedId(MediaStore.Downloads.ExternalContentUri, id);
                    }
                }
            }
        } catch {
            // Fall back to FileProvider
        }
        return null;
    }

    /// <summary>
    /// Returns the display file name of an Android <see cref="Android.Net.Uri"/>.
    /// </summary>
    public static string GetDisplayName(Context context, Android.Net.Uri uri) {
        try {
            using ICursor cursor = context.ContentResolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor != null && cursor.MoveToFirst()) {
                int idx = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (idx >= 0) {
                    return cursor.GetString(idx);
                }
            }
        } catch {
            // Ignore query failures on custom providers
        }
        return uri.LastPathSegment;
    }

    static long GetFileSize(Context context, Android.Net.Uri uri) {
        try {
            using ICursor cursor = context.ContentResolver.Query(uri, [IOpenableColumns.Size], null, null, null);
            if (cursor != null && cursor.MoveToFirst()) {
                int idx = cursor.GetColumnIndex(IOpenableColumns.Size);
                if (idx >= 0) {
                    return cursor.GetLong(idx);
                }
            }
        } catch {
            // Ignore
        }
        return -1;
    }

    public static string TryGetDirectPath(Context context, Android.Net.Uri uri) {
        try {
            if (DocumentsContract.IsDocumentUri(context, uri)) {
                string docId = DocumentsContract.GetDocumentId(uri);
                if ("com.android.externalstorage.documents".Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(docId)) {
                    string[] split = docId.Split(':', 2);
                    if (split.Length == 2 && "primary".Equals(split[0], StringComparison.OrdinalIgnoreCase)) {
                        string root = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
                        return Path.Combine(root, split[1]);
                    }
                } else if ("com.android.providers.downloads.documents".Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrEmpty(docId) && docId.StartsWith("raw:", StringComparison.OrdinalIgnoreCase)) {
                    return docId[4..];
                } else if ("com.android.providers.media.documents".Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrEmpty(docId)) {
                    string[] split = docId.Split(':', 2);
                    if (split.Length == 2) {
                        Android.Net.Uri mediaUri = split[0] switch {
                            "audio" => MediaStore.Audio.Media.ExternalContentUri,
                            "video" => MediaStore.Video.Media.ExternalContentUri,
                            _ => null
                        };
                        if (mediaUri != null) {
                            using ICursor mc = context.ContentResolver.Query(mediaUri, ["_data"], "_id = ?", [split[1]], null);
                            if (mc != null && mc.MoveToFirst()) {
                                int col = mc.GetColumnIndex("_data");
                                if (col >= 0) {
                                    return mc.GetString(col);
                                }
                            }
                        }
                    }
                }
            }

            using ICursor cursor = context.ContentResolver.Query(uri, ["_data"], null, null, null);
            if (cursor != null && cursor.MoveToFirst()) {
                int col = cursor.GetColumnIndex("_data");
                if (col >= 0) {
                    return cursor.GetString(col);
                }
            }
        } catch {
            // Scoped storage or non-file provider
        }
        return null;
    }

    /// <summary>
    /// Returns the original readable directory of <paramref name="uri"/> (before cache fallback copy) if known,
    /// otherwise falls back to the directory of <paramref name="resolvedPath"/> when it is not an app cache folder.
    /// </summary>
    public static string TryGetOriginalDirectory(Context context, Android.Net.Uri uri, string resolvedPath) {
        string direct = uri != null ? TryGetDirectPath(context, uri) : null;
        if (!string.IsNullOrEmpty(direct)) {
            string dir = Path.GetDirectoryName(direct);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                return dir;
            }
        }
        if (!string.IsNullOrEmpty(resolvedPath)) {
            string dir = Path.GetDirectoryName(resolvedPath);
            string cacheRoot = context?.CacheDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                (string.IsNullOrEmpty(cacheRoot) || !dir.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))) {
                return dir;
            }
        }
        return null;
    }

    static bool CanOpenForRead(string path) {
        try {
            if (!File.Exists(path)) {
                return false;
            }
            using FileStream fs = File.OpenRead(path);
            return fs.CanRead;
        } catch {
            return false;
        }
    }

    static bool CanWriteToDirectory(string dir) {
        try {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".cavern_probe_{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return true;
        } catch {
            return false;
        }
    }

    public static string GetMimeType(string fileName) {
        string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch {
            ".apk" => "application/vnd.android.package-archive",
            ".wav" or ".amb" => "audio/wav",
            ".eac3" or ".ec3" => "audio/eac3",
            ".ac3" => "audio/ac3",
            ".flac" => "audio/flac",
            ".opus" => "audio/opus",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".mp3" => "audio/mpeg",
            ".wv" => "audio/x-wavpack",
            ".iamf" => "application/octet-stream",
            ".mkv" => "video/x-matroska",
            ".mka" => "audio/x-matroska",
            ".mp4" => "video/mp4",
            ".json" => "application/json",
            ".srt" or ".vtt" or ".ass" or ".lrc" or ".txt" or ".log" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
