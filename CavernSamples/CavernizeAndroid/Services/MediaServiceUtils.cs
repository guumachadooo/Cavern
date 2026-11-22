using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Android.Graphics;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Shared timed text cue parsed from SRT or WebVTT streams.
/// </summary>
public readonly record struct TimedTextCue(double StartSec, double EndSec, string Text);

/// <summary>
/// Shared safe I/O, encoding, bitmap scaling, and timed-text parsing helpers for Android media services.
/// </summary>
public static partial class MediaServiceUtils {
    [GeneratedRegex(@"(\d{1,2}:\d{1,2}(?::\d{1,2})?(?:[.,]\d{1,7})?)\s*-->\s*(\d{1,2}:\d{1,2}(?::\d{1,2})?(?:[.,]\d{1,7})?)", RegexOptions.Compiled)]
    private static partial Regex SrtVttTimingLineRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex AngleTagRegex();

    [GeneratedRegex(@"\{[^}]*\}|<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex RichSubtitleTagRegex();

    /// <summary>
    /// Decodes a byte buffer using BOM detection (UTF-8, UTF-16 LE/BE), strict UTF-8 validation, and Latin-1 fallback.
    /// </summary>
    public static string ReadTextAutoEncoding(byte[] bytes) {
        if (bytes == null || bytes.Length == 0) {
            return "";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try {
            UTF8Encoding strictUtf8 = new(false, true);
            return strictUtf8.GetString(bytes);
        } catch {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Reads a text file and decodes it with <see cref="ReadTextAutoEncoding(byte[])"/>.
    /// </summary>
    public static string ReadFileTextAutoEncoding(string path) {
        if (string.IsNullOrEmpty(path) || !IOFile.Exists(path)) {
            return "";
        }
        return ReadTextAutoEncoding(IOFile.ReadAllBytes(path));
    }

    /// <summary>
    /// Parses SRT or WebVTT subtitle/lyric cues safely using <see cref="CultureInfo.InvariantCulture"/> and <see cref="TimeSpan.TryParse(string, IFormatProvider, out TimeSpan)"/>.
    /// </summary>
    public static List<TimedTextCue> ParseSrtOrVttCues(
        string raw,
        bool stripCurlyTags = false,
        bool requireEndAfterStart = true,
        string lineSeparator = "\n") {
        List<TimedTextCue> cues = [];
        if (string.IsNullOrWhiteSpace(raw)) {
            return cues;
        }

        string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int i = 0;

        while (i < lines.Length) {
            string line = lines[i].Trim();
            Match m = SrtVttTimingLineRegex().Match(line);
            if (!m.Success) {
                i++;
                continue;
            }

            if (!TryParseSrtVttTimestamp(m.Groups[1].Value, out double start) ||
                !TryParseSrtVttTimestamp(m.Groups[2].Value, out double end)) {
                i++;
                continue;
            }
            i++;

            StringBuilder body = new();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) {
                string curTrimmed = lines[i].Trim();
                if (SrtVttTimingLineRegex().IsMatch(curTrimmed)) {
                    break;
                }
                if (i + 1 < lines.Length && IsAllAsciiDigits(curTrimmed) && SrtVttTimingLineRegex().IsMatch(lines[i + 1])) {
                    break;
                }
                if (body.Length > 0) {
                    body.Append(lineSeparator);
                }
                body.Append(curTrimmed);
                i++;
            }

            string rawText = body.ToString();
            string clean = (stripCurlyTags
                ? RichSubtitleTagRegex().Replace(rawText, "")
                : AngleTagRegex().Replace(rawText, "")).Trim();

            if (!string.IsNullOrWhiteSpace(clean) && (requireEndAfterStart ? end > start : end >= 0)) {
                cues.Add(new TimedTextCue(start, end, clean));
            }
        }

        cues.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return cues;
    }

    static bool TryParseSrtVttTimestamp(string token, out double seconds) {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(token)) {
            return false;
        }
        string normalized = token.Trim().Replace(',', '.');
        int firstColon = normalized.IndexOf(':');
        if (firstColon < 0) {
            return false;
        }
        int secondColon = normalized.IndexOf(':', firstColon + 1);
        if (secondColon < 0) {
            normalized = "00:" + normalized;
        }
        if (!TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out TimeSpan ts) || ts < TimeSpan.Zero) {
            return false;
        }
        seconds = ts.TotalSeconds;
        return true;
    }

    static bool IsAllAsciiDigits(string text) {
        if (string.IsNullOrEmpty(text)) {
            return false;
        }
        for (int i = 0; i < text.Length; i++) {
            if (text[i] < '0' || text[i] > '9') {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Atomically writes UTF-8 text to <paramref name="path"/> via a temporary file in the same directory.
    /// </summary>
    public static void WriteTextAtomic(string path, string content) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        string dir = IOPath.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }
        string tmp = IOPath.Combine(dir ?? "", $".{IOPath.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try {
            IOFile.WriteAllText(tmp, content ?? "", Encoding.UTF8);
            IOFile.Move(tmp, path, overwrite: true);
        } finally {
            try {
                if (IOFile.Exists(tmp)) {
                    IOFile.Delete(tmp);
                }
            } catch {
                // Best-effort temp cleanup
            }
        }
    }

    /// <summary>
    /// Atomically writes raw bytes to <paramref name="path"/> via a temporary file in the same directory.
    /// </summary>
    public static void WriteBytesAtomic(string path, byte[] data) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        string dir = IOPath.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }
        string tmp = IOPath.Combine(dir ?? "", $".{IOPath.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try {
            IOFile.WriteAllBytes(tmp, data ?? []);
            IOFile.Move(tmp, path, overwrite: true);
        } finally {
            try {
                if (IOFile.Exists(tmp)) {
                    IOFile.Delete(tmp);
                }
            } catch {
                // Best-effort temp cleanup
            }
        }
    }

    /// <summary>
    /// Safely decodes and downscales an image byte array to at most <paramref name="maxDim"/> pixels along its longest edge.
    /// </summary>
    public static Bitmap DecodeScaledBitmapSafe(byte[] data, int maxDim) {
        if (data == null || data.Length == 0 || maxDim <= 0) {
            return null;
        }
        try {
            BitmapFactory.Options bounds = new() { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(data, 0, data.Length, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) {
                return null;
            }

            int sample = 1;
            while ((bounds.OutWidth / sample) > maxDim * 2 || (bounds.OutHeight / sample) > maxDim * 2) {
                sample *= 2;
            }

            BitmapFactory.Options opts = new() {
                InSampleSize = sample,
                InPreferredConfig = Bitmap.Config.Argb8888
            };
            Bitmap decoded = BitmapFactory.DecodeByteArray(data, 0, data.Length, opts);
            return ScaleDecodedBitmapAndDisposeTemp(decoded, maxDim);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Safely decodes and downscales an image file on disk to at most <paramref name="maxDim"/> pixels along its longest edge.
    /// </summary>
    public static Bitmap DecodeScaledBitmapSafe(string path, int maxDim) {
        if (string.IsNullOrEmpty(path) || maxDim <= 0 || !IOFile.Exists(path)) {
            return null;
        }
        try {
            BitmapFactory.Options bounds = new() { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) {
                return null;
            }

            int sample = 1;
            while ((bounds.OutWidth / sample) > maxDim * 2 || (bounds.OutHeight / sample) > maxDim * 2) {
                sample *= 2;
            }

            BitmapFactory.Options opts = new() {
                InSampleSize = sample,
                InPreferredConfig = Bitmap.Config.Argb8888
            };
            Bitmap decoded = BitmapFactory.DecodeFile(path, opts);
            return ScaleDecodedBitmapAndDisposeTemp(decoded, maxDim);
        } catch {
            return null;
        }
    }

    static Bitmap ScaleDecodedBitmapAndDisposeTemp(Bitmap decoded, int maxDim) {
        if (decoded == null) {
            return null;
        }
        try {
            if (decoded.Width <= 0 || decoded.Height <= 0) {
                decoded.Dispose();
                return null;
            }
            int longest = Math.Max(decoded.Width, decoded.Height);
            if (longest <= maxDim) {
                return decoded;
            }

            float scale = maxDim / (float)longest;
            int w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            int h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            Bitmap scaled = Bitmap.CreateScaledBitmap(decoded, w, h, true);
            if (!ReferenceEquals(scaled, decoded)) {
                decoded.Dispose();
            }
            return scaled;
        } catch {
            decoded.Dispose();
            return null;
        }
    }
}
