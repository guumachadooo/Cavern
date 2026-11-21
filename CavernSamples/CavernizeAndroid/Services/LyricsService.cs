using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Single timed (or untimed) lyric line parsed from LRC, ELRC, TTML, SRT/VTT, TXT, or embedded media tags.
/// </summary>
public readonly record struct LyricCue(double TimeSeconds, string Text, double? EndTimeSeconds = null);

/// <summary>
/// Parsed lyrics payload ready for Apple Music-style synced scrolling or static display.
/// </summary>
public sealed class LyricsDocument {
    public bool IsSynced { get; init; }
    public IReadOnlyList<LyricCue> Lines { get; init; } = [];
    public string RawUnsyncedText { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public bool HasLyrics => Lines.Count > 0 || !string.IsNullOrWhiteSpace(RawUnsyncedText);
}

/// <summary>
/// Loads lyrics from adjacent <c>.lrc</c>/<c>.txt</c> files or embedded container tags
/// (ID3v2 USLT/SYLT, FLAC/Vorbis comments, MP4 ©lyr, and FFmpeg metadata fallback).
/// </summary>
public static partial class LyricsService {
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"<(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled)]
    private static partial Regex WordTimestampRegex();

    [GeneratedRegex(@"^\[(ti|ar|al|au|by|re|ve|length|la):(.*)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex(@"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OffsetTagRegex();

    [GeneratedRegex(@"<p\b[^>]*\bbegin\s*=\s*[""']([^""']+)[""'][^>]*?(?:\bend\s*=\s*[""']([^""']+)[""'])?[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex TtmlLineRegex();

    static readonly string[] LyricExtensions = [".lrc", ".elrc", ".ttml", ".srt", ".vtt", ".txt"];

    public static LyricsDocument LoadForMedia(Context ctx, Android.Net.Uri sourceUri, string sourceMediaPath, string fallbackTitle, string artist = "") {
        return LoadForMediaCore(ctx, sourceUri, sourceMediaPath, fallbackTitle, artist, ffmpegAsync: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static Task<LyricsDocument> LoadForMediaAsync(
        Context ctx,
        Android.Net.Uri sourceUri,
        string sourceMediaPath,
        string fallbackTitle,
        string artist = "",
        CancellationToken ct = default) {
        return LoadForMediaCore(ctx, sourceUri, sourceMediaPath, fallbackTitle, artist, ffmpegAsync: true, ct);
    }

    static async Task<LyricsDocument> LoadForMediaCore(
        Context ctx,
        Android.Net.Uri sourceUri,
        string sourceMediaPath,
        string fallbackTitle,
        string artist,
        bool ffmpegAsync,
        CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        Android.Net.Uri effectiveUri = sourceUri ?? TryParseUri(sourceMediaPath);

        // 1. Adjacent external .lrc / .txt files in original folder or media folder
        LyricsDocument external = TryLoadAdjacentFile(effectiveCtx, effectiveUri, sourceMediaPath, fallbackTitle);
        if (external != null && external.HasLyrics) {
            return external;
        }

        // 2. Embedded container tags inside file bytes (ID3v2 USLT/SYLT, Vorbis LYRICS, MP4 ©lyr)
        string readablePath = ResolveReadableMediaPath(effectiveCtx, effectiveUri, sourceMediaPath);
        if (!string.IsNullOrEmpty(readablePath) && CanOpenFileForRead(readablePath)) {
            LyricsDocument embedded = TryExtractEmbeddedBinaryTags(readablePath);
            if (embedded != null && embedded.HasLyrics) {
                return embedded;
            }

            // 3. FFmpeg ffmetadata probe fallback (if bundled libffmpeg.so is available in NativeLibraryDir)
            LyricsDocument ffLyrics = ffmpegAsync
                ? await TryExtractWithFFmpegAsync(effectiveCtx, readablePath, ct).ConfigureAwait(false)
                : await Task.Run(() => TryExtractWithFFmpegAsync(effectiveCtx, readablePath, ct), ct).ConfigureAwait(false);
            if (ffLyrics != null && ffLyrics.HasLyrics) {
                return ffLyrics;
            }
        }

        // 4. Online-fetched cached .lrc in app storage
        string cachedLrcPath = OnlineMetadataService.GetCachedLyricsPath(effectiveCtx, fallbackTitle, artist);
        if (!string.IsNullOrEmpty(cachedLrcPath)) {
            try {
                if (IOFile.Exists(cachedLrcPath)) {
                    string raw = MediaServiceUtils.ReadFileTextAutoEncoding(cachedLrcPath);
                    LyricsDocument doc = ParseLyricsText(raw, "Online Cache");
                    if (doc.HasLyrics) {
                        return doc;
                    }
                }
            } catch {
                // Ignore unreadable cache file
            }
        }

        return new LyricsDocument();
    }

    static Android.Net.Uri TryParseUri(string pathOrUri) {
        if (string.IsNullOrWhiteSpace(pathOrUri)) {
            return null;
        }
        if (pathOrUri.StartsWith("content://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
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

    static LyricsDocument TryLoadAdjacentFile(Context ctx, Android.Net.Uri sourceUri, string sourceMediaPath, string fallbackTitle) {
        try {
            List<string> dirs = [];
            string origDir = AndroidFileHelper.TryGetOriginalDirectory(ctx, sourceUri, sourceMediaPath);
            if (!string.IsNullOrEmpty(origDir)) {
                dirs.Add(origDir);
            }
            string directPath = sourceUri != null ? AndroidFileHelper.TryGetDirectPath(ctx, sourceUri) : null;
            if (!string.IsNullOrEmpty(sourceMediaPath) && !IsUriString(sourceMediaPath)) {
                try {
                    string resolvedDir = IOPath.GetDirectoryName(sourceMediaPath);
                    string cacheRoot = ctx?.CacheDir?.AbsolutePath;
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
            if (!string.IsNullOrEmpty(directPath)) {
                AddStem(IOPath.GetFileNameWithoutExtension(directPath));
            }
            if (sourceUri != null && ctx != null) {
                string disp = AndroidFileHelper.GetDisplayName(ctx, sourceUri);
                if (!string.IsNullOrWhiteSpace(disp)) {
                    AddStem(IOPath.GetFileNameWithoutExtension(disp));
                }
            }
            if (!string.IsNullOrEmpty(sourceMediaPath) && !IsUriString(sourceMediaPath)) {
                AddStem(IOPath.GetFileNameWithoutExtension(sourceMediaPath));
            }
            AddStem(fallbackTitle);

            foreach (string dir in dirs) {
                foreach (string stem in stems) {
                    foreach (string ext in LyricExtensions) {
                        string exact = IOPath.Combine(dir, stem + ext);
                        try {
                            if (CanOpenFileForRead(exact)) {
                                string raw = MediaServiceUtils.ReadFileTextAutoEncoding(exact);
                                LyricsDocument doc = ParseLyricsText(raw, IOPath.GetFileName(exact));
                                if (doc.HasLyrics) {
                                    return doc;
                                }
                            }
                        } catch {
                            // Scoped storage may block non-media sibling files
                        }
                    }
                }

                // Case-insensitive directory match (helps on some external mounts / case differences)
                try {
                    foreach (string ext in LyricExtensions) {
                        foreach (string file in Directory.EnumerateFiles(dir, "*" + ext)) {
                            string name = IOPath.GetFileNameWithoutExtension(file);
                            if (stems.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) {
                                string raw = MediaServiceUtils.ReadFileTextAutoEncoding(file);
                                LyricsDocument doc = ParseLyricsText(raw, IOPath.GetFileName(file));
                                if (doc.HasLyrics) {
                                    return doc;
                                }
                            }
                        }
                    }
                } catch {
                    // Directory listing may be restricted by scoped storage
                }
            }
        } catch (Exception ex) {
            AppLogger.Error("LyricsService.TryLoadAdjacentFile", ex);
        }
        return null;
    }

    static LyricsDocument TryExtractEmbeddedBinaryTags(string path) {
        try {
            ParsedAudioTags tags = AudioTagReader.TryReadTags(path);
            if (string.IsNullOrWhiteSpace(tags?.Lyrics)) {
                return null;
            }
            string ext = IOPath.GetExtension(path ?? "").ToLowerInvariant();
            string label = ext switch {
                ".mp3" => "ID3v2",
                ".flac" => "Vorbis Tag",
                ".m4a" or ".mp4" or ".m4v" or ".mov" or ".qt" => "MP4 ©lyr",
                _ => "Embedded Tags"
            };
            LyricsDocument doc = ParseLyricsText(tags.Lyrics, label);
            return doc.HasLyrics ? doc : null;
        } catch (Exception ex) {
            AppLogger.Error("LyricsService.TryExtractEmbeddedBinaryTags", ex);
            return null;
        }
    }

    static async Task<LyricsDocument> TryExtractWithFFmpegAsync(Context ctx, string mediaPath, CancellationToken ct = default) {
        try {
            ct.ThrowIfCancellationRequested();
            string ffmpeg = FFmpegInstallerService.ResolveExistingFFmpegPath(ctx, null);
            if (string.IsNullOrEmpty(ffmpeg) || !IOFile.Exists(ffmpeg)) {
                return null;
            }
            ProcessStartInfo psi = new() {
                FileName = ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("ffmetadata");
            psi.ArgumentList.Add("-");
            FFmpegInstallerService.ConfigureProcessStartInfo(psi, ffmpeg);

            using Process proc = Process.Start(psi);
            if (proc == null) return null;

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(4000);
            using CancellationTokenRegistration ctr = timeoutCts.Token.Register(() => {
                try {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                } catch { }
            });

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            string stdout = stdoutTask.Result;
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

            string rawVal = ExtractKeyFromFFMetadata(stdout, "lyrics");
            if (string.IsNullOrWhiteSpace(rawVal)) {
                return null;
            }
            return ParseLyricsText(rawVal, "Container Tags");
        } catch {
            return null;
        }
    }

    static string ExtractKeyFromFFMetadata(string metadata, string keyPrefix) {
        string[] lines = metadata.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            string line = lines[i];
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line[..eq].Trim();
            if (!k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            StringBuilder val = new(line[(eq + 1)..]);
            while (val.Length > 0 && val[^1] == '\\' && i + 1 < lines.Length) {
                val.Length -= 1;
                val.Append('\n');
                val.Append(lines[++i]);
            }
            return val.ToString()
                .Replace("\\\\", "\\")
                .Replace("\\=", "=")
                .Replace("\\;", ";")
                .Replace("\\#", "#");
        }
        return null;
    }

    public static LyricsDocument ParseLyricsText(string raw, string sourceLabel) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return new LyricsDocument();
        }
        string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        // Check TTML XML format (explicit begin + end markers)
        if (normalized.Contains("<p", StringComparison.OrdinalIgnoreCase) && normalized.Contains("begin=", StringComparison.OrdinalIgnoreCase)) {
            List<LyricCue> ttmlCues = ParseTtmlCues(normalized);
            if (ttmlCues.Count >= 1) {
                return new LyricsDocument {
                    IsSynced = true,
                    Lines = ttmlCues,
                    RawUnsyncedText = string.Join("\n", ttmlCues.Select(c => c.Text)),
                    SourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "TTML" : sourceLabel
                };
            }
        }

        // Check SRT / WebVTT format (explicit start --> end markers)
        if (normalized.Contains("-->", StringComparison.Ordinal)) {
            List<TimedTextCue> parsed = MediaServiceUtils.ParseSrtOrVttCues(normalized, stripCurlyTags: false, requireEndAfterStart: false, lineSeparator: " ");
            if (parsed.Count >= 1) {
                List<LyricCue> srtCues = new(parsed.Count);
                for (int i = 0; i < parsed.Count; i++) {
                    TimedTextCue c = parsed[i];
                    srtCues.Add(new LyricCue(c.StartSec, c.Text, c.EndSec > c.StartSec ? c.EndSec : null));
                }
                return new LyricsDocument {
                    IsSynced = true,
                    Lines = srtCues,
                    RawUnsyncedText = string.Join("\n", srtCues.Select(c => c.Text)),
                    SourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "Timed Subtitles" : sourceLabel
                };
            }
        }

        string[] rawLines = normalized.Split('\n');

        double offsetSec = 0;
        List<LyricCue> synced = [];
        List<string> unsynced = [];

        foreach (string lineRaw in rawLines) {
            string line = lineRaw.Trim();
            if (string.IsNullOrEmpty(line)) {
                continue;
            }

            Match offM = OffsetTagRegex().Match(line);
            if (offM.Success && int.TryParse(offM.Groups[1].Value, out int offMs)) {
                offsetSec = offMs / 1000.0;
                continue;
            }
            if (MetaTagRegex().IsMatch(line)) {
                continue;
            }

            MatchCollection matches = TimestampRegex().Matches(line);
            if (matches.Count > 0) {
                int textStart = matches[^1].Index + matches[^1].Length;
                string rawAfter = TimestampRegex().Replace(line[textStart..], "");

                // Extract Enhanced LRC inline word/end timestamps (<mm:ss.xx>) if present
                double? inlineEndSec = null;
                MatchCollection wordMatches = WordTimestampRegex().Matches(rawAfter);
                if (wordMatches.Count > 0) {
                    Match lastWord = wordMatches[^1];
                    if (TryParseMinSecMatch(lastWord, out double wSec)) {
                        inlineEndSec = Math.Max(0, wSec + offsetSec);
                    }
                    rawAfter = WordTimestampRegex().Replace(rawAfter, "");
                }

                string lyricText = Regex.Replace(rawAfter, @"\s{2,}", " ").Trim();
                foreach (Match m in matches) {
                    if (!TryParseMinSecMatch(m, out double baseSec)) {
                        continue;
                    }
                    double t = Math.Max(0, baseSec + offsetSec);
                    bool isEmptyBreak = string.IsNullOrEmpty(lyricText) || lyricText is "..." or "…" or "• • •" or "•";
                    if (isEmptyBreak) {
                        // Empty LRC break line marks the end of the previous sung phrase
                        if (synced.Count > 0 && !synced[^1].EndTimeSeconds.HasValue && t > synced[^1].TimeSeconds) {
                            LyricCue prev = synced[^1];
                            synced[^1] = prev with { EndTimeSeconds = t };
                        }
                        synced.Add(new LyricCue(t, "• • •"));
                    } else {
                        double? endT = inlineEndSec.HasValue && inlineEndSec.Value > t + 0.15 ? inlineEndSec : null;
                        synced.Add(new LyricCue(t, lyricText, endT));
                    }
                }
            } else {
                unsynced.Add(line);
            }
        }

        if (synced.Count >= 2) {
            synced.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            return new LyricsDocument {
                IsSynced = true,
                Lines = synced,
                RawUnsyncedText = string.Join("\n", synced.Select(c => c.Text)),
                SourceLabel = sourceLabel
            };
        }

        List<LyricCue> staticCues = unsynced
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new LyricCue(-1, x))
            .ToList();
        return new LyricsDocument {
            IsSynced = false,
            Lines = staticCues,
            RawUnsyncedText = string.Join("\n", unsynced),
            SourceLabel = sourceLabel
        };
    }

    static bool TryParseMinSecMatch(Match m, out double seconds) {
        seconds = 0;
        if (!int.TryParse(m.Groups[1].Value, out int mins) ||
            !int.TryParse(m.Groups[2].Value, out int secs)) {
            return false;
        }
        double frac = 0;
        if (m.Groups[3].Success) {
            string fs = m.Groups[3].Value;
            if (double.TryParse(fs, out double fVal)) {
                frac = fVal / Math.Pow(10, fs.Length);
            }
        }
        seconds = mins * 60.0 + secs + frac;
        return true;
    }

    static List<LyricCue> ParseTtmlCues(string normalized) {
        List<LyricCue> cues = [];
        foreach (Match m in TtmlLineRegex().Matches(normalized)) {
            if (!TryParseClockTimestamp(m.Groups[1].Value, out double start)) continue;
            double? end = m.Groups[2].Success && TryParseClockTimestamp(m.Groups[2].Value, out double e) && e > start
                ? e
                : null;
            string inner = WordTimestampRegex().Replace(m.Groups[3].Value, " ");
            inner = Regex.Replace(inner, @"<[^>]+>", " ");
            inner = System.Net.WebUtility.HtmlDecode(inner);
            inner = Regex.Replace(inner, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(inner)) {
                cues.Add(new LyricCue(start, inner, end));
            }
        }
        cues.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return cues;
    }

    static bool TryParseClockTimestamp(string raw, out double seconds) {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secOnly)) {
            seconds = secOnly;
            return true;
        }
        string[] parts = s.Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sc)) {
            seconds = m * 60.0 + sc;
            return true;
        }
        if (parts.Length == 3 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hr) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mn) &&
            double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sc3)) {
            seconds = hr * 3600.0 + mn * 60.0 + sc3;
            return true;
        }
        return false;
    }
}
