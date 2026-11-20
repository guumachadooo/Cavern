using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Internal ContentProvider that exposes downloaded update APKs from CacheDir/updates to the Android package installer.
/// </summary>
[ContentProvider(["hu.sbence.cavern.cavernize.fileprovider"], Exported = false, GrantUriPermissions = true)]
public sealed class ApkFileProvider : ContentProvider {
    public const string Authority = "hu.sbence.cavern.cavernize.fileprovider";

    public static Android.Net.Uri GetApkUri(string fileName) {
        string safeName = Path.GetFileName(fileName);
        return Android.Net.Uri.Parse($"content://{Authority}/updates/{Android.Net.Uri.Encode(safeName)}");
    }

    public static string GetUpdatesDirectory(Context context) {
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "updates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public override bool OnCreate() => true;

    public override string GetType(Android.Net.Uri uri) {
        string name = uri?.LastPathSegment ?? "";
        return name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.android.package-archive"
            : "application/octet-stream";
    }

    public override ParcelFileDescriptor OpenFile(Android.Net.Uri uri, string mode) {
        if (Context == null || uri == null) {
            throw new Java.IO.FileNotFoundException("Invalid APK URI.");
        }
        string safeName = Path.GetFileName(uri.LastPathSegment ?? "");
        if (string.IsNullOrEmpty(safeName)) {
            throw new Java.IO.FileNotFoundException("Empty APK file name.");
        }
        string fullPath = Path.Combine(GetUpdatesDirectory(Context), safeName);
        if (!File.Exists(fullPath)) {
            throw new Java.IO.FileNotFoundException($"Update file not found: {safeName}");
        }
        return ParcelFileDescriptor.Open(new Java.IO.File(fullPath), ParcelFileMode.ReadOnly);
    }

    public override ICursor Query(Android.Net.Uri uri, string[] projection, string selection, string[] selectionArgs, string sortOrder) {
        if (Context == null || uri == null) {
            return null;
        }
        string safeName = Path.GetFileName(uri.LastPathSegment ?? "");
        string fullPath = Path.Combine(GetUpdatesDirectory(Context), safeName);
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

    public override int Delete(Android.Net.Uri uri, string selection, string[] selectionArgs) => 0;

    public override Android.Net.Uri Insert(Android.Net.Uri uri, ContentValues values) => null;

    public override int Update(Android.Net.Uri uri, ContentValues values, string selection, string[] selectionArgs) => 0;
}
