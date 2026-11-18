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

    [GeneratedRegex(@"(?:(\d{1,2}):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{2})[.,](\d{1,3})", RegexOptions.Compiled)]
    private static partial Regex SrtTimingRegex();

    static readonly string[] LyricExtensions = [".lrc", ".elrc", ".ttml", ".srt", ".vtt", ".txt"];

    public static LyricsDocument LoadForMedia(Context ctx, Android.Net.Uri sourceUri, string sourceMediaPath, string fallbackTitle, string artist = "") {
        // 1. Adjacent external .lrc / .txt files in original folder or media folder
        LyricsDocument external = TryLoadAdjacentFile(ctx, sourceUri, sourceMediaPath, fallbackTitle);
        if (external != null && external.HasLyrics) {
            return external;
        }

        // 2. Embedded container tags inside file bytes (ID3v2 USLT/SYLT, Vorbis LYRICS, MP4 ©lyr)
        if (!string.IsNullOrEmpty(sourceMediaPath) && IOFile.Exists(sourceMediaPath)) {
            LyricsDocument embedded = TryExtractEmbeddedBinaryTags(sourceMediaPath);
            if (embedded != null && embedded.HasLyrics) {
                return embedded;
            }

            // 3. FFmpeg ffmetadata probe fallback (if FFmpeg binary is installed in FilesDir)
            LyricsDocument ffLyrics = TryExtractWithFFmpeg(ctx, sourceMediaPath);
            if (ffLyrics != null && ffLyrics.HasLyrics) {
                return ffLyrics;
            }
        }

        // 4. Online-fetched cached .lrc in app storage
        string cachedLrcPath = OnlineMetadataService.GetCachedLyricsPath(ctx, fallbackTitle, artist);
        if (!string.IsNullOrEmpty(cachedLrcPath) && IOFile.Exists(cachedLrcPath)) {
            string raw = ReadTextAutoEncoding(cachedLrcPath);
            LyricsDocument doc = ParseLyricsText(raw, "Online Cache");
            if (doc.HasLyrics) {
                return doc;
            }
        }

        return new LyricsDocument();
    }

    static LyricsDocument TryLoadAdjacentFile(Context ctx, Android.Net.Uri sourceUri, string sourceMediaPath, string fallbackTitle) {
        try {
            List<string> dirs = [];
            string origDir = AndroidFileHelper.TryGetOriginalDirectory(ctx, sourceUri, sourceMediaPath);
            if (!string.IsNullOrEmpty(origDir)) {
                dirs.Add(origDir);
            }
            string directPath = sourceUri != null ? AndroidFileHelper.TryGetDirectPath(ctx, sourceUri) : null;
            if (!string.IsNullOrEmpty(sourceMediaPath)) {
                string resolvedDir = IOPath.GetDirectoryName(sourceMediaPath);
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
            if (!string.IsNullOrEmpty(directPath)) {
                AddStem(IOPath.GetFileNameWithoutExtension(directPath));
            }
            if (!string.IsNullOrEmpty(sourceMediaPath)) {
                AddStem(IOPath.GetFileNameWithoutExtension(sourceMediaPath));
            }
            AddStem(fallbackTitle);

            foreach (string dir in dirs) {
                foreach (string stem in stems) {
                    foreach (string ext in LyricExtensions) {
                        string exact = IOPath.Combine(dir, stem + ext);
                        if (IOFile.Exists(exact)) {
                            string raw = ReadTextAutoEncoding(exact);
                            LyricsDocument doc = ParseLyricsText(raw, IOPath.GetFileName(exact));
                            if (doc.HasLyrics) {
                                return doc;
                            }
                        }
                    }
                }

                // Case-insensitive directory match (helps on some external mounts / case differences)
                try {
                    foreach (string ext in LyricExtensions) {
                        foreach (string file in Directory.EnumerateFiles(dir, "*" + ext)) {
                            string name = IOPath.GetFileNameWithoutExtension(file);
                            if (stems.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) {
                                string raw = ReadTextAutoEncoding(file);
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
            UTF8Encoding strictUtf8 = new(false, true);
            return strictUtf8.GetString(bytes);
        } catch {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    static LyricsDocument TryExtractEmbeddedBinaryTags(string path) {
        try {
            using FileStream fs = IOFile.OpenRead(path);
            long len = fs.Length;
            int headLen = (int)Math.Min(len, 2 * 1024 * 1024);
            byte[] head = new byte[headLen];
            int read = 0;
            while (read < headLen) {
                int n = fs.Read(head, read, headLen - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < headLen) {
                Array.Resize(ref head, read);
            }

            // ID3v2 header check ("ID3")
            if (head.Length >= 10 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3') {
                LyricsDocument id3 = ParseId3v2Lyrics(head);
                if (id3 != null && id3.HasLyrics) {
                    return id3;
                }
            }

            // FLAC / Ogg Vorbis Comment scan
            string vorbisText = ScanVorbisLyrics(head);
            if (!string.IsNullOrWhiteSpace(vorbisText)) {
                LyricsDocument doc = ParseLyricsText(vorbisText, "Vorbis Tag");
                if (doc.HasLyrics) {
                    return doc;
                }
            }

            // MP4 / M4A ©lyr atom (in head or tail moov box)
            string mp4Text = ScanMp4LyricsAtom(head);
            if (string.IsNullOrWhiteSpace(mp4Text) && len > headLen) {
                int tailLen = (int)Math.Min(len - headLen, 2 * 1024 * 1024);
                byte[] tail = new byte[tailLen];
                fs.Seek(len - tailLen, SeekOrigin.Begin);
                int tRead = 0;
                while (tRead < tailLen) {
                    int n = fs.Read(tail, tRead, tailLen - tRead);
                    if (n <= 0) break;
                    tRead += n;
                }
                if (tRead > 0) {
                    mp4Text = ScanMp4LyricsAtom(tail.AsSpan(0, tRead));
                }
            }
            if (!string.IsNullOrWhiteSpace(mp4Text)) {
                LyricsDocument doc = ParseLyricsText(mp4Text, "MP4 ©lyr");
                if (doc.HasLyrics) {
                    return doc;
                }
            }
        } catch (Exception ex) {
            AppLogger.Error("LyricsService.TryExtractEmbeddedBinaryTags", ex);
        }
        return null;
    }

    static LyricsDocument ParseId3v2Lyrics(byte[] data) {
        if (data.Length < 10) return null;
        byte verMajor = data[3];
        if (verMajor < 3 || verMajor > 4) return null;

        int tagSize = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
        int limit = Math.Min(data.Length, 10 + tagSize);
        int pos = 10;

        // Skip extended header if flag bit 6 set
        if ((data[5] & 0x40) != 0 && pos + 4 <= limit) {
            int extSize = verMajor == 4
                ? ((data[pos] & 0x7F) << 21) | ((data[pos + 1] & 0x7F) << 14) | ((data[pos + 2] & 0x7F) << 7) | (data[pos + 3] & 0x7F)
                : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4)) + 4;
            pos += Math.Max(4, extSize);
        }

        string fallbackUslt = null;
        while (pos + 10 <= limit) {
            if (data[pos] == 0) break;
            string frameId = Encoding.ASCII.GetString(data, pos, 4);
            int frameSize = verMajor == 4
                ? ((data[pos + 4] & 0x7F) << 21) | ((data[pos + 5] & 0x7F) << 14) | ((data[pos + 6] & 0x7F) << 7) | (data[pos + 7] & 0x7F)
                : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos + 4, 4));
            pos += 10;
            if (frameSize <= 0 || pos + frameSize > limit) break;

            if (frameId == "SYLT") {
                LyricsDocument syltDoc = ParseId3SyltFrame(data.AsSpan(pos, frameSize));
                if (syltDoc != null && syltDoc.HasLyrics) {
                    return syltDoc;
                }
            } else if (frameId == "USLT" && string.IsNullOrWhiteSpace(fallbackUslt)) {
                fallbackUslt = ParseId3UsltFrame(data.AsSpan(pos, frameSize));
            }
            pos += frameSize;
        }

        return !string.IsNullOrWhiteSpace(fallbackUslt)
            ? ParseLyricsText(fallbackUslt, "ID3v2 USLT")
            : null;
    }

    static string ParseId3UsltFrame(ReadOnlySpan<byte> frame) {
        if (frame.Length < 5) return null;
        byte enc = frame[0];
        int i = 4; // skip encoding + 3-byte lang
        SkipNullTerminatedString(frame, enc, ref i);
        if (i >= frame.Length) return null;
        return DecodeId3String(frame[i..], enc);
    }

    static LyricsDocument ParseId3SyltFrame(ReadOnlySpan<byte> frame) {
        if (frame.Length < 7) return null;
        byte enc = frame[0];
        byte tsFormat = frame[4]; // 2 = milliseconds
        int i = 6; // enc(1) + lang(3) + tsFormat(1) + contentType(1)
        SkipNullTerminatedString(frame, enc, ref i);

        List<LyricCue> cues = [];
        int stepNull = (enc == 1 || enc == 2) ? 2 : 1;
        while (i < frame.Length) {
            int strStart = i;
            int termPos = FindNullTerminator(frame, enc, i);
            if (termPos < 0 || termPos + stepNull + 4 > frame.Length) break;

            string txt = DecodeId3String(frame[strStart..termPos], enc).Trim('\0', '\r', '\n');
            int tsPos = termPos + stepNull;
            uint tsVal = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tsPos, 4));
            double sec = tsFormat == 2 ? tsVal / 1000.0 : tsVal / 25.0;
            if (!string.IsNullOrWhiteSpace(txt)) {
                cues.Add(new LyricCue(sec, txt));
            }
            i = tsPos + 4;
        }
        if (cues.Count < 2) return null;
        cues.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return new LyricsDocument {
            IsSynced = true,
            Lines = cues,
            SourceLabel = "ID3v2 SYLT"
        };
    }

    static void SkipNullTerminatedString(ReadOnlySpan<byte> frame, byte enc, ref int idx) {
        int stepNull = (enc == 1 || enc == 2) ? 2 : 1;
        int end = FindNullTerminator(frame, enc, idx);
        idx = end >= 0 ? end + stepNull : frame.Length;
    }

    static int FindNullTerminator(ReadOnlySpan<byte> frame, byte enc, int start) {
        if (enc == 1 || enc == 2) {
            for (int i = start; i + 1 < frame.Length; i += 2) {
                if (frame[i] == 0 && frame[i + 1] == 0) return i;
            }
            return -1;
        }
        for (int i = start; i < frame.Length; i++) {
            if (frame[i] == 0) return i;
        }
        return -1;
    }

    static string DecodeId3String(ReadOnlySpan<byte> bytes, byte enc) {
        if (bytes.IsEmpty) return "";
        Encoding decoder = enc switch {
            0 => Encoding.Latin1,
            1 => Encoding.Unicode,
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.UTF8,
            _ => Encoding.UTF8
        };
        if (enc == 1 && bytes.Length >= 2) {
            if (bytes[0] == 0xFE && bytes[1] == 0xFF) {
                decoder = Encoding.BigEndianUnicode;
                bytes = bytes[2..];
            } else if (bytes[0] == 0xFF && bytes[1] == 0xFE) {
                decoder = Encoding.Unicode;
                bytes = bytes[2..];
            }
        }
        return decoder.GetString(bytes).TrimEnd('\0');
    }

    static string ScanVorbisLyrics(ReadOnlySpan<byte> data) {
        string[] keys = ["SYNCEDLYRICS=", "LYRICS=", "UNSYNCEDLYRICS="];
        foreach (string key in keys) {
            int idx = IndexOfAsciiCaseInsensitive(data, key);
            if (idx >= 4) {
                int len = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(idx - 4, 4));
                if (len > key.Length && len <= 512 * 1024 && idx + len <= data.Length) {
                    return Encoding.UTF8.GetString(data.Slice(idx + key.Length, len - key.Length));
                }
            }
        }
        return null;
    }

    static string ScanMp4LyricsAtom(ReadOnlySpan<byte> data) {
        ReadOnlySpan<byte> lyrMarker = [0xA9, (byte)'l', (byte)'y', (byte)'r'];
        int pos = data.IndexOf(lyrMarker);
        if (pos < 0 || pos + 20 >= data.Length) return null;

        ReadOnlySpan<byte> tail = data[(pos + 4)..Math.Min(data.Length, pos + 4096)];
        ReadOnlySpan<byte> dataMarker = [(byte)'d', (byte)'a', (byte)'t', (byte)'a'];
        int dIdx = tail.IndexOf(dataMarker);
        if (dIdx < 4 || dIdx + 12 >= tail.Length) return null;

        int atomLen = BinaryPrimitives.ReadInt32BigEndian(tail.Slice(dIdx - 4, 4));
        int payloadStart = dIdx + 12; // 'data' (4) + flags/type (4) + locale (4)
        int payloadLen = Math.Min(atomLen - 16, tail.Length - payloadStart);
        if (payloadLen <= 0) return null;
        return Encoding.UTF8.GetString(tail.Slice(payloadStart, payloadLen)).Trim();
    }

    static int IndexOfAsciiCaseInsensitive(ReadOnlySpan<byte> haystack, string asciiNeedle) {
        int n = asciiNeedle.Length;
        for (int i = 0; i <= haystack.Length - n; i++) {
            bool match = true;
            for (int j = 0; j < n; j++) {
                byte b = haystack[i + j];
                char c = asciiNeedle[j];
                if (b >= 'a' && b <= 'z') b = (byte)(b - 32);
                if (c >= 'a' && c <= 'z') c = (char)(c - 32);
                if (b != c) {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    static LyricsDocument TryExtractWithFFmpeg(Context ctx, string mediaPath) {
        try {
            string ffmpeg = FFmpegInstallerService.GetManagedFFmpegPath(ctx);
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
            var run = FFmpegInstallerService.RunProcessSafe(psi, 4000);
            if (!run.Started || string.IsNullOrWhiteSpace(run.StdOut)) return null;

            string rawVal = ExtractKeyFromFFMetadata(run.StdOut, "lyrics");
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
            List<LyricCue> srtCues = ParseSrtOrVttCues(normalized);
            if (srtCues.Count >= 1) {
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

    static List<LyricCue> ParseSrtOrVttCues(string normalized) {
        List<LyricCue> cues = [];
        string[] blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (string blk in blocks) {
            string[] lines = blk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int timingIdx = -1;
            double start = 0;
            double end = 0;
            for (int i = 0; i < Math.Min(3, lines.Length); i++) {
                Match tm = SrtTimingRegex().Match(lines[i]);
                if (!tm.Success) continue;
                int h1 = tm.Groups[1].Success ? int.Parse(tm.Groups[1].Value) : 0;
                int m1 = int.Parse(tm.Groups[2].Value);
                int s1 = int.Parse(tm.Groups[3].Value);
                double f1 = double.Parse(tm.Groups[4].Value) / Math.Pow(10, tm.Groups[4].Value.Length);
                int h2 = tm.Groups[5].Success ? int.Parse(tm.Groups[5].Value) : 0;
                int m2 = int.Parse(tm.Groups[6].Value);
                int s2 = int.Parse(tm.Groups[7].Value);
                double f2 = double.Parse(tm.Groups[8].Value) / Math.Pow(10, tm.Groups[8].Value.Length);
                start = h1 * 3600 + m1 * 60 + s1 + f1;
                end = h2 * 3600 + m2 * 60 + s2 + f2;
                timingIdx = i;
                break;
            }
            if (timingIdx < 0 || timingIdx + 1 >= lines.Length) continue;
            string txt = string.Join(" ", lines[(timingIdx + 1)..]);
            txt = Regex.Replace(txt, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(txt)) {
                cues.Add(new LyricCue(start, txt, end > start ? end : null));
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
