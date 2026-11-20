using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using CavernizeAndroid.Audio;

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

    [GeneratedRegex(@"(?:(\d{1,2}):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{2})[.,](\d{1,3})", RegexOptions.Compiled)]
    private static partial Regex SrtTimeRangeRegex();

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

        try {
            List<string> dirs = [];
            string origDir = AndroidFileHelper.TryGetOriginalDirectory(ctx, sourceUri, mediaPath);
            if (!string.IsNullOrEmpty(origDir)) {
                dirs.Add(origDir);
            }
            string directPath = sourceUri != null ? AndroidFileHelper.TryGetDirectPath(ctx, sourceUri) : null;
            if (!string.IsNullOrEmpty(mediaPath)) {
                string resolvedDir = IOPath.GetDirectoryName(mediaPath);
                string cacheRoot = ctx?.CacheDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(resolvedDir) &&
                    Directory.Exists(resolvedDir) &&
                    (string.IsNullOrEmpty(cacheRoot) || !resolvedDir.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) &&
                    !dirs.Contains(resolvedDir, StringComparer.OrdinalIgnoreCase)) {
                    dirs.Add(resolvedDir);
                }
            }

            List<string> stems = [];
            void AddStem(string s) {
                if (!string.IsNullOrWhiteSpace(s) && !stems.Contains(s, StringComparer.OrdinalIgnoreCase)) {
                    stems.Add(s);
                }
            }
            if (!string.IsNullOrEmpty(directPath)) AddStem(IOPath.GetFileNameWithoutExtension(directPath));
            if (!string.IsNullOrEmpty(mediaPath)) AddStem(IOPath.GetFileNameWithoutExtension(mediaPath));
            AddStem(fallbackTitle);

            foreach (string dir in dirs) {
                try {
                    foreach (string ext in SubExtensions) {
                        foreach (string file in Directory.EnumerateFiles(dir, "*" + ext)) {
                            string nameNoExt = IOPath.GetFileNameWithoutExtension(file);
                            bool stemMatch = stems.Any(s =>
                                nameNoExt.StartsWith(s, StringComparison.OrdinalIgnoreCase) ||
                                s.StartsWith(nameNoExt, StringComparison.OrdinalIgnoreCase));
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

        if (ffmpeg != null && ffmpeg.Found && !string.IsNullOrEmpty(mediaPath) && IOFile.Exists(mediaPath)) {
            try {
                result.AddRange(ProbeEmbeddedSubtitles(ffmpeg.Location, mediaPath));
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
            string defTag = extra.Contains("default", StringComparison.OrdinalIgnoreCase) ? " (Padrão)" : "";
            list.Add(new SubtitleTrackInfo {
                Id = $"emb:{streamIdx}",
                Title = $"Stream #{streamIdx}{langTag} • {codec.ToUpperInvariant()}{defTag}",
                Language = lang,
                EmbeddedStreamIndex = streamIdx
            });
        }
        return list;
    }

    public static List<SubtitleCue> LoadCues(
        Context ctx,
        SubtitleTrackInfo track,
        string mediaPath,
        AndroidFFmpegRunner ffmpeg) {
        if (track == null) {
            return [];
        }

        if (!string.IsNullOrEmpty(track.ExternalFilePath) && IOFile.Exists(track.ExternalFilePath)) {
            string ext = IOPath.GetExtension(track.ExternalFilePath).ToLowerInvariant();
            string raw = ReadTextAutoEncoding(track.ExternalFilePath);
            return ext is ".ass" or ".ssa" ? ParseAss(raw) : ParseSrtOrVtt(raw);
        }

        if (track.EmbeddedStreamIndex >= 0 && ffmpeg != null && ffmpeg.Found && !string.IsNullOrEmpty(mediaPath)) {
            string subDir = IOPath.Combine(ctx.CacheDir.AbsolutePath, "subtitles");
            Directory.CreateDirectory(subDir);
            string outSrt = IOPath.Combine(subDir, $"track_{track.EmbeddedStreamIndex}.srt");
            try {
                if (IOFile.Exists(outSrt)) {
                    IOFile.Delete(outSrt);
                }
            } catch {
                // Ignore
            }

            ProcessStartInfo psi = new() {
                FileName = ffmpeg.Location,
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
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add($"0:{track.EmbeddedStreamIndex}");
            psi.ArgumentList.Add("-c:s");
            psi.ArgumentList.Add("srt");
            psi.ArgumentList.Add(outSrt);

            FFmpegInstallerService.RunProcessSafe(psi, 25000);

            if (IOFile.Exists(outSrt)) {
                string raw = ReadTextAutoEncoding(outSrt);
                return ParseSrtOrVtt(raw);
            }
        }

        return [];
    }

    public static List<SubtitleCue> LoadFromFilePath(string filePath) {
        if (string.IsNullOrEmpty(filePath) || !IOFile.Exists(filePath)) {
            return [];
        }
        string ext = IOPath.GetExtension(filePath).ToLowerInvariant();
        string raw = ReadTextAutoEncoding(filePath);
        return ext is ".ass" or ".ssa" ? ParseAss(raw) : ParseSrtOrVtt(raw);
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
        List<SubtitleCue> list = [];
        if (string.IsNullOrWhiteSpace(raw)) {
            return list;
        }

        string[] lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length) {
            string line = lines[i].Trim();
            Match m = SrtTimeRangeRegex().Match(line);
            if (!m.Success) {
                i++;
                continue;
            }

            double start = ParseTimeGroups(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            double end = ParseTimeGroups(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
            i++;

            StringBuilder body = new();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) {
                if (body.Length > 0) body.Append('\n');
                body.Append(lines[i].Trim());
                i++;
            }

            string clean = CleanSubtitleText(body.ToString());
            if (!string.IsNullOrEmpty(clean) && end > start) {
                list.Add(new SubtitleCue(start, end, clean));
            }
        }

        list.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
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

    static double ParseTimeGroups(string hStr, string mStr, string sStr, string msStr) {
        int h = string.IsNullOrEmpty(hStr) ? 0 : int.Parse(hStr, CultureInfo.InvariantCulture);
        int m = int.Parse(mStr, CultureInfo.InvariantCulture);
        int s = int.Parse(sStr, CultureInfo.InvariantCulture);
        string paddedMs = msStr.PadRight(3, '0')[..3];
        int ms = int.Parse(paddedMs, CultureInfo.InvariantCulture);
        return h * 3600.0 + m * 60.0 + s + ms / 1000.0;
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

    static string ReadTextAutoEncoding(string path) {
        byte[] bytes = IOFile.ReadAllBytes(path);
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
            return new UTF8Encoding(false, true).GetString(bytes);
        } catch {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
