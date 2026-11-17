using Android.Content;
using Android.Database;
using Android.Media;
using Android.OS;
using Android.Provider;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Resolves Android SAF content URIs to readable file paths and publishes exported files to Downloads/Cavernize.
/// </summary>
public static class AndroidFileHelper {
    /// <summary>
    /// Checks whether the app has direct filesystem read/write access to shared external storage.
    /// </summary>
    public static bool HasAllFilesAccess() {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) {
            return Android.OS.Environment.IsExternalStorageManager;
        }
        return true;
    }

    /// <summary>
    /// Resolves an Android <see cref="Android.Net.Uri"/> into a readable local file path.
    /// Attempts zero-copy direct path resolution first; falls back to copying into app cache.
    /// </summary>
    public static string ResolveReadablePath(Context context, Android.Net.Uri uri, Action<double, string> onProgress = null) {
        if (uri == null) {
            return null;
        }

        if ("file".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)) {
            return uri.Path;
        }

        string direct = TryGetDirectPath(context, uri);
        if (!string.IsNullOrEmpty(direct) && CanOpenForRead(direct)) {
            return direct;
        }

        string displayName = GetDisplayName(context, uri) ?? $"cavern_input_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.bin";
        foreach (char c in Path.GetInvalidFileNameChars()) {
            displayName = displayName.Replace(c, '_');
        }

        string cacheFolder = Path.Combine(context.CacheDir.AbsolutePath, "opened");
        Directory.CreateDirectory(cacheFolder);
        string destPath = Path.Combine(cacheFolder, displayName);

        long expectedSize = GetFileSize(context, uri);
        using System.IO.Stream src = context.ContentResolver.OpenInputStream(uri)
            ?? throw new IOException("Cannot open selected file stream.");
        using FileStream dst = File.Create(destPath);

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
        return destPath;
    }

    /// <summary>
    /// Gets the best directory for writing output files (direct Downloads/Cavernize when writable, else app files folder).
    /// </summary>
    public static string GetWorkingOutputDir(Context context, string subfolder = null) {
        string publicDownloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        string candidate = string.IsNullOrEmpty(subfolder)
            ? Path.Combine(publicDownloads ?? context.FilesDir.AbsolutePath, "Cavernize")
            : Path.Combine(publicDownloads ?? context.FilesDir.AbsolutePath, "Cavernize", subfolder);

        if (CanWriteToDirectory(candidate)) {
            return candidate;
        }

        string appDownloads = context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
            ?? context.FilesDir.AbsolutePath;
        string fallback = string.IsNullOrEmpty(subfolder)
            ? Path.Combine(appDownloads, "Cavernize")
            : Path.Combine(appDownloads, "Cavernize", subfolder);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// Ensures a file written to <paramref name="localPath"/> is visible in the public Downloads/Cavernize folder,
    /// returning a MediaStore content URI when available for sharing.
    /// </summary>
    public static Android.Net.Uri PublishToDownloads(Context context, string localPath, string relativeSubfolder = null) {
        if (!File.Exists(localPath)) {
            return null;
        }

        string publicDownloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath ?? "";
        bool alreadyInPublicDownloads = !string.IsNullOrEmpty(publicDownloads) &&
            localPath.StartsWith(publicDownloads, StringComparison.OrdinalIgnoreCase);

        if (alreadyInPublicDownloads) {
            MediaScannerConnection.ScanFile(context, [localPath], null, null);
            return Android.Net.Uri.FromFile(new Java.IO.File(localPath));
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) {
            string fileName = Path.GetFileName(localPath);
            string relPath = string.IsNullOrEmpty(relativeSubfolder)
                ? "Download/Cavernize/"
                : $"Download/Cavernize/{relativeSubfolder.Trim('/')}/";

            ContentValues values = new();
            values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
            values.Put(MediaStore.IMediaColumns.MimeType, GetMimeType(fileName));
            values.Put(MediaStore.IMediaColumns.RelativePath, relPath);
            values.Put(MediaStore.IMediaColumns.IsPending, 1);

            ContentResolver resolver = context.ContentResolver;
            Android.Net.Uri itemUri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
            if (itemUri != null) {
                using (System.IO.Stream outStream = resolver.OpenOutputStream(itemUri))
                using (FileStream inStream = File.OpenRead(localPath)) {
                    inStream.CopyTo(outStream);
                }
                values.Clear();
                values.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(itemUri, values, null, null);
                return itemUri;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a shareable <see cref="Android.Net.Uri"/> (content:// on API 29+) for <paramref name="localPath"/>.
    /// </summary>
    public static Android.Net.Uri GetShareableUri(Context context, string localPath) {
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) {
            return null;
        }
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) {
            string fileName = Path.GetFileName(localPath);
            ContentValues values = new();
            values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
            values.Put(MediaStore.IMediaColumns.MimeType, GetMimeType(fileName));
            values.Put(MediaStore.IMediaColumns.RelativePath, "Download/Cavernize/Shared/");
            values.Put(MediaStore.IMediaColumns.IsPending, 1);

            ContentResolver resolver = context.ContentResolver;
            Android.Net.Uri uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
            if (uri != null) {
                using (System.IO.Stream dst = resolver.OpenOutputStream(uri))
                using (FileStream src = File.OpenRead(localPath)) {
                    src.CopyTo(dst);
                }
                values.Clear();
                values.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(uri, values, null, null);
                return uri;
            }
        }
        return Android.Net.Uri.FromFile(new Java.IO.File(localPath));
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

    static string TryGetDirectPath(Context context, Android.Net.Uri uri) {
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

    static string GetMimeType(string fileName) {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch {
            ".wav" => "audio/wav",
            ".eac3" or ".ec3" => "audio/eac3",
            ".ac3" => "audio/ac3",
            ".flac" => "audio/flac",
            ".opus" => "audio/opus",
            ".mkv" => "video/x-matroska",
            ".mka" => "audio/x-matroska",
            ".mp4" => "video/mp4",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
    }
}
