using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using CavernizeAndroid.Audio;
using CavernizeAndroid.Language;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Single timed subtitle cue extracted from external or embedded subtitle streams.
/// </summary>
public readonly record struct SubtitleCue(double StartSec, double EndSec, string Text);

/// <summary>
/// Describes an available subtitle track (external file or embedded container stream).
/// </summary>
public sealed class SubtitleTrackInfo {
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Language { get; init; } = "";
    public string ExternalFilePath { get; init; } = "";
    public int EmbeddedStreamIndex { get; init; } = -1;
}

/// <summary>
/// Discovers, extracts (via FFmpeg for MKV/MP4), and parses <c>.srt</c>, <c>.vtt</c>, and <c>.ass</c>/<c>.ssa</c>
/// subtitle tracks for real-time overlay inside <see cref="Views.SyncedVideoStageView"/>.
/// </summary>
public static partial class SubtitleService {
    [GeneratedRegex(@"Stream\s+#0:(\d+)(?:\(([^)]+)\))?[^:]*:\s*Subtitle:\s*([a-zA-Z0-9_]+)(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FFmpegSubStreamRegex();

    [GeneratedRegex(@"\{[^}]*\}|<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex RichTagRegex();

    static readonly string[] SubExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    static readonly HashSet<string> BitmapSubCodecs = new(StringComparer.OrdinalIgnoreCase) {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub"
    };

    public static List<SubtitleTrackInfo> DiscoverTracks(
        Context ctx,
        Android.Net.Uri sourceUri,
        string mediaPath,
        string fallbackTitle,
        AndroidFFmpegRunner ffmpeg) {
        List<SubtitleTrackInfo> result = [];
        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        Android.Net.Uri effectiveUri = sourceUri ?? TryParseUri(mediaPath);

        try {
            List<string> dirs = [];
            string origDir = AndroidFileHelper.TryGetOriginalDirectory(effectiveCtx, effectiveUri, mediaPath);
            if (!string.IsNullOrEmpty(origDir)) {
                dirs.Add(origDir);
            }
            string directPath = effectiveUri != null ? AndroidFileHelper.TryGetDirectPath(effectiveCtx, effectiveUri) : null;
            if (!string.IsNullOrEmpty(mediaPath) && !IsUriString(mediaPath)) {
                try {
                    string resolvedDir = IOPath.GetDirectoryName(mediaPath);
                    string cacheRoot = effectiveCtx?.CacheDir?.AbsolutePath;
                    if (!string.IsNullOrEmpty(resolvedDir) &&
                        Directory.Exists(resolvedDir) &&
                        (string.IsNullOrEmpty(cacheRoot) || !resolvedDir.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) &&
                        !dirs.Contains(resolvedDir, StringComparer.OrdinalIgnoreCase)) {
                        dirs.Add(resolvedDir);
                    }
                } catch {
                    // Ignore inaccessible directory under scoped storage
                }
            }

            List<string> stems = [];
            void AddStem(string s) {
                if (!string.IsNullOrWhiteSpace(s) && !stems.Contains(s, StringComparer.OrdinalIgnoreCase)) {
                    stems.Add(s);
                }
            }
            if (!string.IsNullOrEmpty(directPath)) AddStem(IOPath.GetFileNameWithoutExtension(directPath));
            if (effectiveUri != null && effectiveCtx != null) {
                string disp = AndroidFileHelper.GetDisplayName(effectiveCtx, effectiveUri);
                if (!string.IsNullOrWhiteSpace(disp)) {
                    AddStem(IOPath.GetFileNameWithoutExtension(disp));
                }
            }
            if (!string.IsNullOrEmpty(mediaPath) && !IsUriString(mediaPath)) AddStem(IOPath.GetFileNameWithoutExtension(mediaPath));
            AddStem(fallbackTitle);

            foreach (string dir in dirs) {
                try {
                    foreach (string ext in SubExtensions) {
                        foreach (string file in Directory.EnumerateFiles(dir, "*" + ext)) {
                            string nameNoExt = IOPath.GetFileNameWithoutExtension(file);
                            bool stemMatch = stems.Any(s => IsStrictStemMatch(nameNoExt, s));
                            if (!stemMatch || !seenFiles.Add(file)) {
                                continue;
                            }
                            result.Add(new SubtitleTrackInfo {
                                Id = "ext:" + file,
                                Title = IOPath.GetFileName(file),
                                ExternalFilePath = file
                            });
                        }
                    }
                } catch {
                    // Scoped storage may restrict directory enumeration
                }
            }
        } catch (Exception ex) {
            AppLogger.Error("SubtitleService.DiscoverExternal", ex);
        }

        string ffmpegPath = ResolveFFmpegPath(effectiveCtx, ffmpeg);
        string localMediaPath = ResolveReadableMediaPath(effectiveCtx, effectiveUri, mediaPath);
        if (!string.IsNullOrEmpty(ffmpegPath) && !string.IsNullOrEmpty(localMediaPath)) {
            try {
                result.AddRange(ProbeEmbeddedSubtitles(ffmpegPath, localMediaPath));
            } catch (Exception ex) {
                AppLogger.Error("SubtitleService.ProbeEmbedded", ex);
            }
        }

        return result;
    }

    static List<SubtitleTrackInfo> ProbeEmbeddedSubtitles(string ffmpegPath, string mediaPath) {
        List<SubtitleTrackInfo> list = [];
        ProcessStartInfo psi = new() {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaPath);

        var (_, _, err) = FFmpegInstallerService.RunProcessSafe(psi, 8000);

        foreach (string line in err.Split('\n')) {
            Match m = FFmpegSubStreamRegex().Match(line);
            if (!m.Success) {
                continue;
            }
            if (!int.TryParse(m.Groups[1].Value, out int streamIdx)) {
                continue;
            }
            string lang = m.Groups[2].Value.Trim();
            string codec = m.Groups[3].Value.Trim().ToLowerInvariant();
            if (BitmapSubCodecs.Contains(codec)) {
                continue;
            }
            string extra = m.Groups[4].Value.Trim();
            string langTag = string.IsNullOrEmpty(lang) ? "" : $" [{lang.ToUpperInvariant()}]";
            string defTag = extra.Contains("default", StringComparison.OrdinalIgnoreCase)
                ? $" ({AndroidStrings.Active["SubtitleDefaultTag"]})"
                : "";
            list.Add(new SubtitleTrackInfo {
                Id = $"emb:{streamIdx}",
                Title = $"Stream #{streamIdx}{langTag} • {codec.ToUpperInvariant()}{defTag}",
                Language = lang,
                EmbeddedStreamIndex = streamIdx
            });
        }
        return list;
    }

    static bool IsStrictStemMatch(string fileStem, string mediaStem) {
        if (string.IsNullOrWhiteSpace(fileStem) || string.IsNullOrWhiteSpace(mediaStem)) {
            return false;
        }
        if (fileStem.Equals(mediaStem, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        if (fileStem.Length > mediaStem.Length &&
            fileStem.StartsWith(mediaStem, StringComparison.OrdinalIgnoreCase)) {
            char sep = fileStem[mediaStem.Length];
            return sep is '.' or '-' or '_' or ' ' or '(' or '[';
        }
        return false;
    }

    public static List<SubtitleCue> LoadCues(
        Context ctx,
        SubtitleTrackInfo track,
        string mediaPath,
        AndroidFFmpegRunner ffmpeg) {
        if (track == null) {
            return [];
        }
        Context effectiveCtx = ctx ?? Android.App.Application.Context;

        if (!string.IsNullOrEmpty(track.ExternalFilePath)) {
            List<SubtitleCue> extCues = LoadFromFilePath(effectiveCtx, track.ExternalFilePath);
            if (extCues.Count > 0 || track.EmbeddedStreamIndex < 0) {
                return extCues;
            }
        }

        string ffmpegPath = ResolveFFmpegPath(effectiveCtx, ffmpeg);
        string localMediaPath = track.EmbeddedStreamIndex >= 0
            ? ResolveReadableMediaPath(effectiveCtx, TryParseUri(mediaPath), mediaPath)
            : "";

        if (track.EmbeddedStreamIndex >= 0 && !string.IsNullOrEmpty(ffmpegPath) && !string.IsNullOrEmpty(localMediaPath)) {
            string cacheRoot = effectiveCtx?.CacheDir?.AbsolutePath ?? effectiveCtx?.FilesDir?.AbsolutePath ?? IOPath.GetTempPath();
            string subDir = IOPath.Combine(cacheRoot, "subtitles");
            Directory.CreateDirectory(subDir);
            string mediaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(localMediaPath)))[..12];
            string outSrt = IOPath.Combine(subDir, $"track_{mediaHash}_{track.EmbeddedStreamIndex}.srt");
            try {
                if (IOFile.Exists(outSrt)) {
                    IOFile.Delete(outSrt);
                }
            } catch {
                // Ignore
            }

            ProcessStartInfo psi = new() {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(localMediaPath);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add($"0:{track.EmbeddedStreamIndex}");
            psi.ArgumentList.Add("-c:s");
            psi.ArgumentList.Add("srt");
            psi.ArgumentList.Add(outSrt);

            FFmpegInstallerService.RunProcessSafe(psi, 25000);

            if (IOFile.Exists(outSrt)) {
                string raw = MediaServiceUtils.ReadFileTextAutoEncoding(outSrt);
                return ParseSrtOrVtt(raw);
            }
        }

        return [];
    }

    public static List<SubtitleCue> LoadFromFilePath(string filePath) =>
        LoadFromFilePath(Android.App.Application.Context, filePath);

    public static List<SubtitleCue> LoadFromFilePath(Context ctx, string filePath) {
        if (string.IsNullOrEmpty(filePath)) {
            return [];
        }
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        Android.Net.Uri uri = TryParseUri(filePath);
        if (uri != null && effectiveCtx != null) {
            try {
                using Stream stream = AndroidFileHelper.OpenReadableStream(effectiveCtx, uri);
                if (stream != null) {
                    using MemoryStream ms = new();
                    stream.CopyTo(ms);
                    string rawUriText = MediaServiceUtils.ReadTextAutoEncoding(ms.ToArray());
                    string disp = AndroidFileHelper.GetDisplayName(effectiveCtx, uri) ?? "";
                    string uriExt = IOPath.GetExtension(disp).ToLowerInvariant();
                    bool isAss = uriExt is ".ass" or ".ssa" ||
                        (string.IsNullOrEmpty(uriExt) && rawUriText.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase));
                    return isAss ? ParseAss(rawUriText) : ParseSrtOrVtt(rawUriText);
                }
            } catch {
                // Fall back to direct file check below if possible
            }
        }

        try {
            if (!CanOpenFileForRead(filePath)) {
                return [];
            }
            string ext = IOPath.GetExtension(filePath).ToLowerInvariant();
            string raw = MediaServiceUtils.ReadFileTextAutoEncoding(filePath);
            return ext is ".ass" or ".ssa" ? ParseAss(raw) : ParseSrtOrVtt(raw);
        } catch {
            return [];
        }
    }

    static string ResolveFFmpegPath(Context ctx, AndroidFFmpegRunner ffmpeg) {
        string resolved = FFmpegInstallerService.ResolveExistingFFmpegPath(ctx, null);
        if (!string.IsNullOrEmpty(resolved) && IOFile.Exists(resolved)) {
            return resolved;
        }
        if (ffmpeg != null && ffmpeg.Found && !string.IsNullOrEmpty(ffmpeg.Location) &&
            !IsWritableAppDataPath(ctx, ffmpeg.Location) && IOFile.Exists(ffmpeg.Location)) {
            return ffmpeg.Location;
        }
        return "";
    }

    static bool IsWritableAppDataPath(Context ctx, string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }
        try {
            string full = IOPath.GetFullPath(path);
            string files = ctx?.FilesDir?.AbsolutePath;
            string cache = ctx?.CacheDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(files) && full.StartsWith(IOPath.GetFullPath(files), StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            if (!string.IsNullOrEmpty(cache) && full.StartsWith(IOPath.GetFullPath(cache), StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            return full.Replace('\\', '/').Contains("/files/bin/", StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    static Android.Net.Uri TryParseUri(string pathOrUri) {
        if (string.IsNullOrWhiteSpace(pathOrUri)) {
            return null;
        }
        if (IsUriString(pathOrUri)) {
            try {
                return Android.Net.Uri.Parse(pathOrUri);
            } catch {
                return null;
            }
        }
        return null;
    }

    static bool IsUriString(string value) =>
        !string.IsNullOrEmpty(value) &&
        (value.StartsWith("content://", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

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

    static string ResolveReadableMediaPath(Context ctx, Android.Net.Uri uri, string path) {
        if (CanOpenFileForRead(path)) {
            return path;
        }
        if (uri != null && ctx != null) {
            try {
                string resolved = AndroidFileHelper.ResolveReadablePath(ctx, uri);
                if (CanOpenFileForRead(resolved)) {
                    return resolved;
                }
            } catch {
                // Unreadable SAF/content URI under scoped storage
            }
        }
        return "";
    }

    public static string FindActiveText(IReadOnlyList<SubtitleCue> cues, double currentSeconds) {
        if (cues == null || cues.Count == 0) {
            return "";
        }
        StringBuilder sb = null;
        for (int i = 0; i < cues.Count; i++) {
            SubtitleCue c = cues[i];
            if (currentSeconds >= c.StartSec && currentSeconds <= c.EndSec) {
                sb ??= new StringBuilder();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(c.Text);
            } else if (c.StartSec > currentSeconds + 2.0) {
                // Cues are sorted by StartSec, exit once well past current time
                break;
            }
        }
        return sb?.ToString() ?? "";
    }

    static List<SubtitleCue> ParseSrtOrVtt(string raw) {
        List<TimedTextCue> parsed = MediaServiceUtils.ParseSrtOrVttCues(raw, stripCurlyTags: true, requireEndAfterStart: true, lineSeparator: "\n");
        List<SubtitleCue> list = new(parsed.Count);
        for (int i = 0; i < parsed.Count; i++) {
            TimedTextCue c = parsed[i];
            list.Add(new SubtitleCue(c.StartSec, c.EndSec, c.Text));
        }
        return list;
    }

    static List<SubtitleCue> ParseAss(string raw) {
        List<SubtitleCue> list = [];
        if (string.IsNullOrWhiteSpace(raw)) {
            return list;
        }

        int startCol = 1;
        int endCol = 2;
        int textCol = 9;
        int formatCount = 10;

        string[] lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string rawLine in lines) {
            string line = rawLine.Trim();
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) {
                string[] cols = line[7..].Split(',');
                formatCount = cols.Length;
                for (int c = 0; c < cols.Length; c++) {
                    string name = cols[c].Trim().ToLowerInvariant();
                    if (name == "start") startCol = c;
                    else if (name == "end") endCol = c;
                    else if (name == "text") textCol = c;
                }
            } else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) {
                string payload = line[9..].TrimStart();
                string[] parts = payload.Split(',', Math.Max(formatCount, textCol + 1));
                if (parts.Length <= textCol) continue;

                if (TryParseAssTime(parts[startCol].Trim(), out double start) &&
                    TryParseAssTime(parts[endCol].Trim(), out double end)) {
                    string txt = parts[textCol].Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
                    string clean = CleanSubtitleText(txt);
                    if (!string.IsNullOrEmpty(clean) && end > start) {
                        list.Add(new SubtitleCue(start, end, clean));
                    }
                }
            }
        }

        list.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return list;
    }

    static bool TryParseAssTime(string s, out double sec) {
        sec = 0;
        string[] parts = s.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) {
            return false;
        }
        string[] secParts = parts[2].Split('.', ',');
        if (!int.TryParse(secParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs)) {
            return false;
        }
        int cs = 0;
        if (secParts.Length > 1) {
            int.TryParse(secParts[1].PadRight(2, '0')[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out cs);
        }
        sec = h * 3600.0 + m * 60.0 + secs + cs / 100.0;
        return true;
    }

    static string CleanSubtitleText(string text) {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string stripped = RichTagRegex().Replace(text, "").Trim();
        return stripped;
    }
}
