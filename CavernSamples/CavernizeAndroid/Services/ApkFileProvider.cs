using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Internal ContentProvider that exposes downloaded update APKs and shareable app files via <c>content://</c> URIs
/// using authority <c>hu.sbence.cavern.cavernize.fileprovider</c> (no <c>file://</c> exposure).
/// </summary>
[ContentProvider(["hu.sbence.cavern.cavernize.fileprovider"], Exported = false, GrantUriPermissions = true)]
public sealed class ApkFileProvider : ContentProvider {
    public const string Authority = "hu.sbence.cavern.cavernize.fileprovider";

    /// <summary>Returns the provider authority matching <c>PackageName + ".fileprovider"</c>.</summary>
    public static string GetAuthority(Context context) =>
        !string.IsNullOrEmpty(context?.PackageName) ? $"{context.PackageName}.fileprovider" : Authority;

    public static Android.Net.Uri GetApkUri(string fileName) => GetApkUri(null, fileName);

    public static Android.Net.Uri GetApkUri(Context context, string fileName) {
        string safeName = Path.GetFileName(fileName);
        return new Android.Net.Uri.Builder()
            .Scheme("content")
            .Authority(GetAuthority(context))
            .AppendPath("updates")
            .AppendPath(safeName)
            .Build();
    }

    public static string GetUpdatesDirectory(Context context) {
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "updates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetSharedDirectory(Context context) {
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "shared");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Converts a local file path into a safe <c>content://</c> FileProvider URI without path traversal.
    /// Files outside recognized app/download roots are staged into <c>CacheDir/shared</c> first.
    /// </summary>
    public static Android.Net.Uri GetUriForFile(Context context, string fullPath) {
        if (context == null || string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) {
            return null;
        }
        string canonical = Path.GetFullPath(fullPath);
        foreach ((string bucket, string rootDir) in GetRootBuckets(context)) {
            if (string.IsNullOrEmpty(rootDir)) {
                continue;
            }
            string canonicalRoot = EnsureTrailingSeparator(Path.GetFullPath(rootDir));
            if (canonical.StartsWith(canonicalRoot, StringComparison.Ordinal)) {
                string rel = canonical[canonicalRoot.Length..];
                return BuildBucketUri(context, bucket, rel);
            }
        }

        string safeName = Path.GetFileName(canonical);
        if (string.IsNullOrEmpty(safeName)) {
            return null;
        }
        string stagedPath = Path.Combine(GetSharedDirectory(context), safeName);
        if (!string.Equals(canonical, Path.GetFullPath(stagedPath), StringComparison.Ordinal)) {
            File.Copy(canonical, stagedPath, overwrite: true);
        }
        return BuildBucketUri(context, "shared", safeName);
    }

    public override bool OnCreate() => true;

    public override string GetType(Android.Net.Uri uri) {
        string name = uri?.LastPathSegment ?? "";
        return AndroidFileHelper.GetMimeType(name);
    }

    public override ParcelFileDescriptor OpenFile(Android.Net.Uri uri, string mode) {
        if (!string.IsNullOrEmpty(mode) &&
            !mode.Equals("r", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("rt", StringComparison.OrdinalIgnoreCase)) {
            throw new Java.Lang.SecurityException($"Write mode '{mode}' is not permitted.");
        }
        string fullPath = ResolveSafePath(Context, uri);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) {
            throw new Java.IO.FileNotFoundException($"File not found for URI: {uri}");
        }
        return ParcelFileDescriptor.Open(new Java.IO.File(fullPath), ParcelFileMode.ReadOnly);
    }

    public override ICursor Query(Android.Net.Uri uri, string[] projection, string selection, string[] selectionArgs, string sortOrder) {
        string fullPath = ResolveSafePath(Context, uri);
        if (string.IsNullOrEmpty(fullPath)) {
            return null;
        }
        string safeName = Path.GetFileName(fullPath);
        long size = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;

        string[] cols = projection is { Length: > 0 }
            ? projection
            : [IOpenableColumns.DisplayName, IOpenableColumns.Size];

        MatrixCursor cursor = new(cols, 1);
        Java.Lang.Object[] row = new Java.Lang.Object[cols.Length];
        for (int i = 0; i < cols.Length; i++) {
            if (cols[i] == IOpenableColumns.DisplayName) {
                row[i] = new Java.Lang.String(safeName);
            } else if (cols[i] == IOpenableColumns.Size) {
                row[i] = new Java.Lang.Long(size);
            } else {
                row[i] = null;
            }
        }
        cursor.AddRow(row);
        return cursor;
    }

    static Android.Net.Uri BuildBucketUri(Context context, string bucket, string relativePath) {
        Android.Net.Uri.Builder builder = new Android.Net.Uri.Builder()
            .Scheme("content")
            .Authority(GetAuthority(context))
            .AppendPath(bucket);
        foreach (string seg in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)) {
            builder.AppendPath(seg);
        }
        return builder.Build();
    }

    static (string Bucket, string RootDir)[] GetRootBuckets(Context context) {
        if (context == null) {
            return [];
        }
        string cacheDir = context.CacheDir?.AbsolutePath;
        return [
            ("updates", !string.IsNullOrEmpty(cacheDir) ? Path.Combine(cacheDir, "updates") : null),
            ("shared", !string.IsNullOrEmpty(cacheDir) ? Path.Combine(cacheDir, "shared") : null),
            ("ext_files", context.GetExternalFilesDir(null)?.AbsolutePath),
            ("ext_cache", context.ExternalCacheDir?.AbsolutePath),
            ("files", context.FilesDir?.AbsolutePath),
            ("cache", cacheDir),
            ("public_dl", Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath)
        ];
    }

    static string ResolveSafePath(Context context, Android.Net.Uri uri) {
        if (context == null || uri == null) {
            return null;
        }
        IList<string> segments = uri.PathSegments;
        if (segments == null || segments.Count < 2) {
            return null;
        }
        string bucket = segments[0];
        string matchedRoot = null;
        foreach ((string b, string rootDir) in GetRootBuckets(context)) {
            if (string.Equals(b, bucket, StringComparison.Ordinal) && !string.IsNullOrEmpty(rootDir)) {
                matchedRoot = rootDir;
                break;
            }
        }
        if (string.IsNullOrEmpty(matchedRoot)) {
            return null;
        }

        string[] relSegs = new string[segments.Count - 1];
        for (int i = 1; i < segments.Count; i++) {
            string seg = segments[i] ?? "";
            if (string.IsNullOrEmpty(seg) || seg == "." || seg == ".." ||
                seg.Contains('/') || seg.Contains('\\') || seg.Contains('\0')) {
                return null;
            }
            relSegs[i - 1] = seg;
        }

        string canonicalRoot = EnsureTrailingSeparator(Path.GetFullPath(matchedRoot));
        string candidate = Path.GetFullPath(Path.Combine([canonicalRoot, .. relSegs]));
        return candidate.StartsWith(canonicalRoot, StringComparison.Ordinal) ? candidate : null;
    }

    static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    public override int Delete(Android.Net.Uri uri, string selection, string[] selectionArgs) => 0;

    public override Android.Net.Uri Insert(Android.Net.Uri uri, ContentValues values) => null;

    public override int Update(Android.Net.Uri uri, ContentValues values, string selection, string[] selectionArgs) => 0;
}
