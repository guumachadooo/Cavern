using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Android.Content;

using Cavern.Spatial;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Single timed word or syllable segment within a syllable-synced <see cref="LyricCue"/>.
/// </summary>
public readonly record struct LyricWordCue(double StartSeconds, double EndSeconds, string Text);

/// <summary>
/// Single timed (or untimed) lyric line parsed from LRC, ELRC, TTML, YRC, KRC, SRT/VTT, TXT, or embedded media tags.
/// </summary>
public readonly record struct LyricCue(double TimeSeconds, string Text, double? EndTimeSeconds = null, IReadOnlyList<LyricWordCue> Words = null);

/// <summary>
/// Parsed lyrics payload ready for Apple Music-style synced scrolling, syllable karaoke, or static display.
/// </summary>
public sealed class LyricsDocument {
    public bool IsSynced { get; init; }
    public bool IsSyllableSynced { get; init; }
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

    [GeneratedRegex(@"^\[(ti|ar|al|au|by|re|ve|length|la|id|hash|sign|qq|total|language):(.*)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex(@"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OffsetTagRegex();

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled)]
    private static partial Regex MsLineRegex();

    [GeneratedRegex(@"\((\d+),(\d+),\d+\)", RegexOptions.Compiled)]
    private static partial Regex YrcWordRegex();

    [GeneratedRegex(@"<(\d+),(\d+),\d+>", RegexOptions.Compiled)]
    private static partial Regex KrcWordRegex();

    [GeneratedRegex(@"<p\b([^>]*)>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex TtmlLineRegex();

    [GeneratedRegex(@"<span\b([^>]*)>|</span>|<br\s*/?>|<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex TtmlTokenRegex();

    [GeneratedRegex(@"\b(begin|end|dur)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex XmlAttrRegex();

    static readonly string[] LyricExtensions = [".elrc", ".ttml", ".krc", ".lrc", ".srt", ".vtt", ".txt"];

    static readonly byte[] KrcXorKey = [
        0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
    ];

    /// <summary>Returns sync precision tier: 3 = syllable-synced, 2 = line-synced, 1 = plain untimed, 0 = empty.</summary>
    public static int GetSyncTier(LyricsDocument doc) {
        if (doc == null || !doc.HasLyrics) return 0;
        if (doc.IsSyllableSynced) return 3;
        return doc.IsSynced ? 2 : 1;
    }

    const int MaxMoodDigestSections = 16;

    static readonly HashSet<string> HighArousalTokens = new(StringComparer.OrdinalIgnoreCase) {
        "fire", "burn", "run", "fly", "rise", "shout", "scream", "thunder", "storm", "fight",
        "dance", "jump", "wild", "explode", "power", "free", "alive", "tonight", "higher", "fast",
        "rush", "break", "bang", "crash", "electric", "glow", "shine", "roar", "rage", "heart"
    };

    static readonly HashSet<string> LowArousalTokens = new(StringComparer.OrdinalIgnoreCase) {
        "sleep", "quiet", "silence", "soft", "slow", "whisper", "breathe", "calm", "still", "gentle",
        "drift", "fade", "shadow", "moon", "night", "rest", "peace", "dream", "float", "hush",
        "alone", "hollow", "cold", "rain", "mist", "memory", "sigh", "close", "stay", "hold"
    };

    static readonly HashSet<string> PositiveValenceTokens = new(StringComparer.OrdinalIgnoreCase) {
        "love", "light", "bright", "sun", "shine", "smile", "hope", "joy", "free", "alive",
        "fly", "rise", "dream", "home", "gold", "star", "heaven", "sweet", "together", "win",
        "happy", "grace", "kiss", "warm", "bloom", "magic", "dance", "glory", "forever", "strong"
    };

    static readonly HashSet<string> NegativeValenceTokens = new(StringComparer.OrdinalIgnoreCase) {
        "pain", "cry", "tears", "broken", "dark", "lost", "fear", "hate", "die", "dead",
        "bleed", "hurt", "cold", "alone", "empty", "fall", "shadow", "ghost", "ache", "sorrow",
        "burn", "scar", "lies", "gone", "goodbye", "nightmare", "crash", "trapped", "fade", "grief"
    };

    /// <summary>
    /// Distills T0–T3 lyrics into bounded <see cref="LyricMoodDigest"/> enums and normalized arousal/valence scalars.
    /// Anti-prompt-injection guard: never exposes or forwards any raw lyric text outside this method.
    /// </summary>
    public static LyricMoodDigest[] ExtractMoodDigest(LyricsDocument doc) {
        if (doc == null || !doc.HasLyrics) {
            return [];
        }

        if (!doc.IsSynced || doc.Lines == null || doc.Lines.Count == 0) {
            string raw = doc.RawUnsyncedText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) {
                return [];
            }
            string[] rawLines = raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            List<string> cleanLines = new(rawLines.Length);
            for (int i = 0; i < rawLines.Length; i++) {
                string trimmed = rawLines[i].Trim();
                if (trimmed.Length > 0 && !IsBreakText(trimmed)) {
                    cleanLines.Add(trimmed);
                }
            }
            if (cleanLines.Count == 0) {
                return [];
            }
            int sectionCount = Math.Min(MaxMoodDigestSections, Math.Max(1, (int)Math.Ceiling(cleanLines.Count / 4.0)));
            LyricMoodDigest[] result = new LyricMoodDigest[sectionCount];
            for (int s = 0; s < sectionCount; s++) {
                int startIdx = (s * cleanLines.Count) / sectionCount;
                int endIdx = ((s + 1) * cleanLines.Count) / sectionCount;
                result[s] = ScoreTextSliceToMoodDigest(cleanLines, startIdx, endIdx, wordRateHint: 0f, sectionIndex: s, startTimeSec: 0.0);
            }
            return result;
        }

        // Tier 2 / Tier 3 synced cues: segment by instrumental breaks, gaps >= 6s, or >= 4 lines
        List<LyricMoodDigest> sections = new(MaxMoodDigestSections);
        List<string> bucketLines = new(8);
        double bucketStart = -1.0;
        double lastTime = -1.0;
        int bucketWordCount = 0;
        double bucketWordSpan = 0.0;

        void FlushBucket() {
            if (bucketLines.Count == 0 || sections.Count >= MaxMoodDigestSections) {
                bucketLines.Clear();
                bucketWordCount = 0;
                bucketWordSpan = 0.0;
                bucketStart = -1.0;
                return;
            }
            float wps = bucketWordSpan > 0.25 ? (float)(bucketWordCount / bucketWordSpan) : 0f;
            sections.Add(ScoreTextSliceToMoodDigest(
                bucketLines,
                0,
                bucketLines.Count,
                wps,
                sections.Count,
                bucketStart >= 0.0 ? bucketStart : 0.0));
            bucketLines.Clear();
            bucketWordCount = 0;
            bucketWordSpan = 0.0;
            bucketStart = -1.0;
        }

        IReadOnlyList<LyricCue> cues = doc.Lines;
        int targetLinesPerSection = Math.Max(4, (int)Math.Ceiling(cues.Count / (double)MaxMoodDigestSections));
        for (int i = 0; i < cues.Count; i++) {
            LyricCue cue = cues[i];
            if (string.IsNullOrWhiteSpace(cue.Text) || IsBreakText(cue.Text)) {
                FlushBucket();
                continue;
            }
            if (bucketLines.Count > 0 && lastTime >= 0.0 &&
                ((cue.TimeSeconds - lastTime >= 6.0) || bucketLines.Count >= targetLinesPerSection)) {
                FlushBucket();
            }
            if (bucketStart < 0.0) {
                bucketStart = Math.Max(0.0, cue.TimeSeconds);
            }
            bucketLines.Add(cue.Text);
            if (cue.Words != null && cue.Words.Count > 0) {
                bucketWordCount += cue.Words.Count;
                double span = cue.Words[^1].EndSeconds - cue.Words[0].StartSeconds;
                if (span > 0.05) {
                    bucketWordSpan += span;
                }
            }
            lastTime = Math.Max(lastTime, cue.EndTimeSeconds ?? cue.TimeSeconds);
        }
        FlushBucket();
        return sections.ToArray();
    }

    static LyricMoodDigest ScoreTextSliceToMoodDigest(
        IReadOnlyList<string> lines,
        int startIdx,
        int endIdx,
        float wordRateHint,
        int sectionIndex,
        double startTimeSec) {
        int exclam = 0;
        int quest = 0;
        int upperChars = 0;
        int alphaChars = 0;
        int tokenCount = 0;
        int highHit = 0;
        int lowHit = 0;
        int posHit = 0;
        int negHit = 0;

        StringBuilder tok = new(24);
        void ClassifyToken() {
            if (tok.Length == 0) return;
            string w = tok.ToString();
            tok.Clear();
            tokenCount++;
            if (HighArousalTokens.Contains(w)) highHit++;
            if (LowArousalTokens.Contains(w)) lowHit++;
            if (PositiveValenceTokens.Contains(w)) posHit++;
            if (NegativeValenceTokens.Contains(w)) negHit++;
        }

        for (int l = startIdx; l < endIdx; l++) {
            string line = lines[l];
            for (int c = 0; c < line.Length; c++) {
                char ch = line[c];
                if (ch == '!') exclam++;
                else if (ch == '?') quest++;
                if (char.IsLetter(ch)) {
                    alphaChars++;
                    if (char.IsUpper(ch)) upperChars++;
                    tok.Append(ch);
                } else {
                    ClassifyToken();
                }
            }
            ClassifyToken();
        }

        float upperRatio = alphaChars > 0 ? Math.Clamp(upperChars / (float)alphaChars, 0f, 1f) : 0f;
        float punctBoost = Math.Clamp((exclam * 0.14f) + (quest * 0.05f), 0f, 0.35f);
        float rateBoost = wordRateHint > 0f ? Math.Clamp((wordRateHint - 1.5f) / 3.5f, -0.15f, 0.30f) : 0f;
        float lexArousal = tokenCount > 0 ? Math.Clamp((highHit - lowHit) / Math.Max(2f, tokenCount * 0.25f), -0.35f, 0.35f) : 0f;
        float arousal = Math.Clamp(0.45f + punctBoost + upperRatio * 0.25f + rateBoost + lexArousal, 0f, 1f);

        float lexValence = tokenCount > 0 ? Math.Clamp((posHit - negHit) / Math.Max(2f, tokenCount * 0.22f), -0.45f, 0.45f) : 0f;
        float valence = Math.Clamp(0.52f + lexValence - quest * 0.03f, 0f, 1f);

        LyricMoodTag tag = (arousal >= 0.62f && valence >= 0.50f) ? LyricMoodTag.Anthem
            : (arousal >= 0.56f && valence < 0.50f) ? LyricMoodTag.Tense
            : (arousal < 0.56f && valence < 0.48f) ? LyricMoodTag.Intimate
            : LyricMoodTag.Calm;

        return new LyricMoodDigest(tag, arousal, valence, sectionIndex, startTimeSec);
    }

    /// <summary>Fast check whether raw lyric text contains syllable/word-level timing markers (ELRC, YRC, KRC, or TTML spans).</summary>
    public static bool HasSyllableTags(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (YrcWordRegex().IsMatch(raw) || KrcWordRegex().IsMatch(raw)) return true;
        if (raw.Contains("<p", StringComparison.OrdinalIgnoreCase) &&
            raw.Contains("<span", StringComparison.OrdinalIgnoreCase) &&
            raw.Contains("begin=", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        foreach (string line in raw.Replace("\r\n", "\n").Split('\n')) {
            MatchCollection lineTs = TimestampRegex().Matches(line);
            if (lineTs.Count == 0) continue;
            string after = line[(lineTs[^1].Index + lineTs[^1].Length)..];
            MatchCollection wm = WordTimestampRegex().Matches(after);
            if (wm.Count >= 2 || (wm.Count == 1 && !string.IsNullOrWhiteSpace(after[(wm[0].Index + wm[0].Length)..]))) {
                return true;
            }
        }
        return false;
    }

    /// <summary>Decodes KuGou <c>krc1</c> XOR+zlib bytes or plain UTF-8 KRC text.</summary>
    public static string TryDecodeKrcBytes(byte[] bytes) {
        if (bytes == null || bytes.Length == 0) return "";
        if (bytes.Length > 4 && bytes[0] == (byte)'k' && bytes[1] == (byte)'r' && bytes[2] == (byte)'c' && bytes[3] == (byte)'1') {
            try {
                byte[] decoded = new byte[bytes.Length - 4];
                for (int i = 0; i < decoded.Length; i++) {
                    decoded[i] = (byte)(bytes[i + 4] ^ KrcXorKey[i & 15]);
                }
                using MemoryStream ms = new(decoded);
                using ZLibStream z = new(ms, CompressionMode.Decompress);
                using StreamReader sr = new(z, Encoding.UTF8);
                return sr.ReadToEnd();
            } catch {
                return "";
            }
        }
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

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

        LyricsDocument best = null;
        int bestTier = 0;
        void Consider(LyricsDocument candidate) {
            int tier = GetSyncTier(candidate);
            if (tier > bestTier) {
                best = candidate;
                bestTier = tier;
            }
        }

        // 1. Adjacent external .elrc / .ttml / .krc / .lrc / .srt / .txt files
        Consider(TryLoadAdjacentFile(effectiveCtx, effectiveUri, sourceMediaPath, fallbackTitle));
        if (bestTier == 3) {
            return best;
        }

        // Load online cache early (fast local read) so tier 3 cache beats spawning FFmpeg or embedded plain lyrics
        LyricsDocument cachedDoc = TryLoadCachedLyrics(effectiveCtx, fallbackTitle, artist);

        // 2. Embedded container tags inside file bytes (ID3v2 USLT/SYLT, Vorbis LYRICS, MP4 ©lyr)
        string readablePath = ResolveReadableMediaPath(effectiveCtx, effectiveUri, sourceMediaPath);
        if (!string.IsNullOrEmpty(readablePath) && CanOpenFileForRead(readablePath)) {
            Consider(TryExtractEmbeddedBinaryTags(readablePath));
            if (bestTier == 3) {
                return best;
            }

            if (GetSyncTier(cachedDoc) == 3) {
                return cachedDoc;
            }

            // 3. FFmpeg ffmetadata probe fallback (if bundled libffmpeg.so is available in NativeLibraryDir)
            LyricsDocument ffLyrics = ffmpegAsync
                ? await TryExtractWithFFmpegAsync(effectiveCtx, readablePath, ct).ConfigureAwait(false)
                : await Task.Run(() => TryExtractWithFFmpegAsync(effectiveCtx, readablePath, ct), ct).ConfigureAwait(false);
            Consider(ffLyrics);
            if (bestTier == 3) {
                return best;
            }
        }

        // 4. Online-fetched cached lyrics (strictly beats lower-tier embedded/adjacent plain text)
        Consider(cachedDoc);
        return best ?? new LyricsDocument();
    }

    static LyricsDocument TryLoadCachedLyrics(Context ctx, string fallbackTitle, string artist) {
        string cachedLrcPath = OnlineMetadataService.GetCachedLyricsPath(ctx, fallbackTitle, artist);
        if (string.IsNullOrEmpty(cachedLrcPath)) return null;
        try {
            if (IOFile.Exists(cachedLrcPath)) {
                string raw = cachedLrcPath.EndsWith(".krc", StringComparison.OrdinalIgnoreCase)
                    ? TryDecodeKrcBytes(IOFile.ReadAllBytes(cachedLrcPath))
                    : MediaServiceUtils.ReadFileTextAutoEncoding(cachedLrcPath);
                LyricsDocument doc = ParseLyricsText(raw, "Online Cache");
                return doc.HasLyrics ? doc : null;
            }
        } catch {
            // Ignore unreadable cache file
        }
        return null;
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

            LyricsDocument ReadLyricFile(string filePath) {
                string ext = IOPath.GetExtension(filePath);
                string raw = ext.Equals(".krc", StringComparison.OrdinalIgnoreCase)
                    ? TryDecodeKrcBytes(IOFile.ReadAllBytes(filePath))
                    : MediaServiceUtils.ReadFileTextAutoEncoding(filePath);
                return ParseLyricsText(raw, IOPath.GetFileName(filePath));
            }

            LyricsDocument bestDoc = null;
            int bestTier = 0;
            void ConsiderDoc(LyricsDocument d) {
                int tier = GetSyncTier(d);
                if (tier > bestTier) {
                    bestDoc = d;
                    bestTier = tier;
                }
            }

            foreach (string dir in dirs) {
                foreach (string stem in stems) {
                    foreach (string ext in LyricExtensions) {
                        string exact = IOPath.Combine(dir, stem + ext);
                        try {
                            if (CanOpenFileForRead(exact)) {
                                ConsiderDoc(ReadLyricFile(exact));
                                if (bestTier == 3) return bestDoc;
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
                                ConsiderDoc(ReadLyricFile(file));
                                if (bestTier == 3) return bestDoc;
                            }
                        }
                    }
                } catch {
                    // Directory listing may be restricted by scoped storage
                }
            }
            if (bestDoc != null) return bestDoc;
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

            using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
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
        normalized = UnwrapNeteaseJsonIfNeeded(normalized);

        // Check TTML XML format (explicit begin + end markers, optional <span> syllable children)
        if (normalized.Contains("<p", StringComparison.OrdinalIgnoreCase) && normalized.Contains("begin=", StringComparison.OrdinalIgnoreCase)) {
            List<LyricCue> ttmlCues = ParseTtmlCues(normalized);
            if (ttmlCues.Count >= 1) {
                InferTrailingLineAndWordEnds(ttmlCues);
                return new LyricsDocument {
                    IsSynced = true,
                    IsSyllableSynced = ttmlCues.Any(c => c.Words is { Count: > 0 }),
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
                    IsSyllableSynced = false,
                    Lines = srtCues,
                    RawUnsyncedText = string.Join("\n", srtCues.Select(c => c.Text)),
                    SourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "Timed Subtitles" : sourceLabel
                };
            }
        }

        string[] rawLines = normalized.Split('\n');

        double offsetSec = 0;
        List<LyricCue> synced = [];
        List<LyricCue> yrcCredits = [];
        List<string> unsynced = [];
        bool sawMsTimedLine = false;

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

            // NetEase YRC JSON credit header line: {"t":0,"c":[{"tx":"..."}]}
            if (line.StartsWith('{') && TryParseNeteaseCreditJsonLine(line, offsetSec, out LyricCue creditCue)) {
                yrcCredits.Add(creditCue);
                continue;
            }

            // NetEase YRC / KuGou KRC millisecond-timed line: [startMs,durMs]...
            Match msM = MsLineRegex().Match(line);
            if (msM.Success && TryParseMsTimedLine(msM, offsetSec, out LyricCue msCue)) {
                sawMsTimedLine = true;
                if (IsBreakText(msCue.Text)) {
                    ClosePreviousCueIfOpen(synced, msCue.TimeSeconds);
                    synced.Add(new LyricCue(msCue.TimeSeconds, "• • •"));
                } else {
                    synced.Add(msCue);
                }
                continue;
            }

            MatchCollection matches = TimestampRegex().Matches(line);
            if (matches.Count > 0) {
                int textStart = matches[^1].Index + matches[^1].Length;
                string rawAfter = TimestampRegex().Replace(line[textStart..], "");

                List<double> lineTimes = [];
                foreach (Match m in matches) {
                    if (TryParseMinSecMatch(m, out double baseSec)) {
                        lineTimes.Add(Math.Max(0, baseSec + offsetSec));
                    }
                }
                if (lineTimes.Count == 0) continue;

                // Parse Enhanced LRC inline word/syllable timestamps (<mm:ss.xx>) when present
                List<LyricWordCue> parsedWords = ParseEnhancedLrcWords(rawAfter, lineTimes[0], offsetSec, out double? inlineEndSec);
                string stripped = WordTimestampRegex().Replace(rawAfter, "");
                string lyricText = parsedWords != null
                    ? Regex.Replace(string.Concat(parsedWords.Select(w => w.Text)), @"[ \t]{2,}", " ").Trim()
                    : Regex.Replace(stripped, @"\s{2,}", " ").Trim();

                double anchorSec = lineTimes[0];
                if (parsedWords != null && parsedWords.Count > 0 && lineTimes.Count > 1) {
                    double firstWordSec = parsedWords[0].StartSeconds;
                    anchorSec = lineTimes.OrderBy(lt => Math.Abs(lt - firstWordSec)).First();
                }

                foreach (double t in lineTimes) {
                    if (IsBreakText(lyricText)) {
                        ClosePreviousCueIfOpen(synced, t);
                        synced.Add(new LyricCue(t, "• • •"));
                    } else {
                        List<LyricWordCue> cueWords = ShiftWordsForRepeat(parsedWords, t - anchorSec);
                        double? endT = inlineEndSec.HasValue && inlineEndSec.Value > t + 0.15
                            ? Math.Max(0, inlineEndSec.Value + (t - anchorSec))
                            : null;
                        synced.Add(new LyricCue(t, lyricText, endT, cueWords));
                    }
                }
            } else {
                unsynced.Add(line);
            }
        }

        if (sawMsTimedLine && yrcCredits.Count > 0) {
            synced.AddRange(yrcCredits);
        }

        if (synced.Count >= 2 || (sawMsTimedLine && synced.Count >= 1)) {
            synced.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            InferTrailingLineAndWordEnds(synced);
            return new LyricsDocument {
                IsSynced = true,
                IsSyllableSynced = synced.Any(c => c.Words is { Count: > 0 }),
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
            IsSyllableSynced = false,
            Lines = staticCues,
            RawUnsyncedText = string.Join("\n", unsynced),
            SourceLabel = sourceLabel
        };
    }

    static bool IsBreakText(string lyricText) =>
        string.IsNullOrEmpty(lyricText) || lyricText is "..." or "…" or "• • •" or "•";

    static void ClosePreviousCueIfOpen(List<LyricCue> synced, double endTimeSec) {
        if (synced.Count > 0 && !synced[^1].EndTimeSeconds.HasValue && endTimeSec > synced[^1].TimeSeconds) {
            synced[^1] = synced[^1] with { EndTimeSeconds = endTimeSec };
        }
    }

    static string UnwrapNeteaseJsonIfNeeded(string normalized) {
        if (!normalized.StartsWith('{')) return normalized;
        if (!normalized.Contains("\"yrc\"", StringComparison.Ordinal) &&
            !normalized.Contains("\"klyric\"", StringComparison.Ordinal) &&
            !normalized.Contains("\"lrc\"", StringComparison.Ordinal)) {
            return normalized;
        }
        try {
            using JsonDocument doc = JsonDocument.Parse(normalized);
            JsonElement root = doc.RootElement;
            foreach (string key in new[] { "yrc", "klyric", "lrc" }) {
                if (root.TryGetProperty(key, out JsonElement obj) &&
                    obj.ValueKind == JsonValueKind.Object &&
                    obj.TryGetProperty("lyric", out JsonElement lyrEl) &&
                    lyrEl.ValueKind == JsonValueKind.String) {
                    string val = lyrEl.GetString();
                    if (!string.IsNullOrWhiteSpace(val) && (key != "yrc" || HasSyllableTags(val))) {
                        return val.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                    }
                }
            }
        } catch {
            // Not JSON lyric envelope; parse as regular text
        }
        return normalized;
    }

    static bool TryParseNeteaseCreditJsonLine(string line, double offsetSec, out LyricCue cue) {
        cue = default;
        if (!line.Contains("\"t\"", StringComparison.Ordinal) || !line.Contains("\"c\"", StringComparison.Ordinal)) {
            return false;
        }
        try {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("t", out JsonElement tEl) || !tEl.TryGetInt64(out long tMs)) return false;
            if (!root.TryGetProperty("c", out JsonElement cArr) || cArr.ValueKind != JsonValueKind.Array) return false;
            StringBuilder sb = new();
            foreach (JsonElement item in cArr.EnumerateArray()) {
                if (item.TryGetProperty("tx", out JsonElement txEl) && txEl.ValueKind == JsonValueKind.String) {
                    sb.Append(txEl.GetString());
                }
            }
            string txt = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(txt)) return false;
            cue = new LyricCue(Math.Max(0, tMs / 1000.0 + offsetSec), txt);
            return true;
        } catch {
            return false;
        }
    }

    static bool TryParseMsTimedLine(Match msM, double offsetSec, out LyricCue cue) {
        cue = default;
        if (!long.TryParse(msM.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long lineStartMs) ||
            !long.TryParse(msM.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long lineDurMs)) {
            return false;
        }
        string rest = msM.Groups[3].Value;
        double lineStartSec = Math.Max(0, lineStartMs / 1000.0 + offsetSec);
        double? lineEndSec = lineDurMs > 0 ? Math.Max(lineStartSec + 0.05, lineStartSec + lineDurMs / 1000.0) : null;

        MatchCollection yrcM = YrcWordRegex().Matches(rest);
        MatchCollection krcM = yrcM.Count == 0 ? KrcWordRegex().Matches(rest) : null;
        MatchCollection tokens = yrcM.Count > 0 ? yrcM : krcM;
        if (tokens == null || tokens.Count == 0) {
            string plainLine = rest.Trim();
            cue = new LyricCue(lineStartSec, plainLine, lineEndSec);
            return true;
        }

        bool isKrc = yrcM.Count == 0;
        bool krcRelative = false;
        if (isKrc && long.TryParse(tokens[0].Groups[1].Value, out long firstRawMs)) {
            krcRelative = !(lineStartMs > 2000 && firstRawMs >= lineStartMs && firstRawMs > lineDurMs + 500);
        }

        List<LyricWordCue> words = [];
        for (int i = 0; i < tokens.Count; i++) {
            Match tm = tokens[i];
            if (!long.TryParse(tm.Groups[1].Value, out long rawTimeMs)) continue;
            long.TryParse(tm.Groups[2].Value, out long durMs);

            long absMs = krcRelative ? lineStartMs + rawTimeMs : rawTimeMs;
            double wStart = Math.Max(0, absMs / 1000.0 + offsetSec);
            double wEnd = durMs > 0 ? wStart + durMs / 1000.0 : wStart;

            int segStart = tm.Index + tm.Length;
            int segEnd = i + 1 < tokens.Count ? tokens[i + 1].Index : rest.Length;
            string segRaw = rest[segStart..segEnd];
            AppendWordSegment(words, segRaw, wStart, wEnd);
        }

        FinalizeWordCues(words, lineEndSec);
        string lineText = words.Count > 0 ? string.Concat(words.Select(w => w.Text)).Trim() : "";
        cue = new LyricCue(lineStartSec, lineText, lineEndSec, words.Count > 0 ? words : null);
        return true;
    }

    static List<LyricWordCue> ParseEnhancedLrcWords(string rawAfter, double anchorLineSec, double offsetSec, out double? inlineEndSec) {
        inlineEndSec = null;
        MatchCollection wordMatches = WordTimestampRegex().Matches(rawAfter);
        if (wordMatches.Count == 0) return null;

        // Single trailing <mm:ss.xx> with no following text is a line-end marker, not syllable sync
        if (wordMatches.Count == 1 && string.IsNullOrWhiteSpace(rawAfter[(wordMatches[0].Index + wordMatches[0].Length)..])) {
            if (TryParseMinSecMatch(wordMatches[0], out double endSec)) {
                inlineEndSec = Math.Max(0, endSec + offsetSec);
            }
            return null;
        }

        List<LyricWordCue> words = [];
        string prefix = rawAfter[..wordMatches[0].Index];
        if (!string.IsNullOrWhiteSpace(prefix) && TryParseMinSecMatch(wordMatches[0], out double firstTagSec)) {
            double firstEnd = Math.Max(anchorLineSec, Math.Max(0, firstTagSec + offsetSec));
            AppendWordSegment(words, Regex.Replace(prefix, @"[ \t]{2,}", " "), anchorLineSec, firstEnd);
        }

        for (int i = 0; i < wordMatches.Count; i++) {
            Match wm = wordMatches[i];
            if (!TryParseMinSecMatch(wm, out double wSec)) continue;
            double segStart = Math.Max(0, wSec + offsetSec);
            int segStartIdx = wm.Index + wm.Length;
            int segEndIdx = i + 1 < wordMatches.Count ? wordMatches[i + 1].Index : rawAfter.Length;
            string segRaw = Regex.Replace(rawAfter[segStartIdx..segEndIdx], @"[ \t]{2,}", " ");

            if (segRaw.Length == 0) {
                if (words.Count > 0 && words[^1].EndSeconds <= words[^1].StartSeconds && segStart > words[^1].StartSeconds) {
                    words[^1] = words[^1] with { EndSeconds = segStart };
                }
                inlineEndSec = segStart;
                continue;
            }
            AppendWordSegment(words, segRaw, segStart, segStart);
        }

        if (words.Count == 0) return null;
        if (words[^1].EndSeconds > words[^1].StartSeconds) {
            inlineEndSec = Math.Max(inlineEndSec ?? 0, words[^1].EndSeconds);
        }
        FinalizeWordCues(words, inlineEndSec);
        return words.Count > 0 ? words : null;
    }

    static void AppendWordSegment(List<LyricWordCue> words, string segRaw, double wStart, double wEnd) {
        if (string.IsNullOrEmpty(segRaw)) return;
        if (string.IsNullOrWhiteSpace(segRaw)) {
            if (words.Count > 0) {
                LyricWordCue prev = words[^1];
                double prevEnd = prev.EndSeconds <= prev.StartSeconds && wStart > prev.StartSeconds ? wStart : prev.EndSeconds;
                string prevTxt = prev.Text.EndsWith(' ') ? prev.Text : prev.Text + " ";
                words[^1] = prev with { EndSeconds = prevEnd, Text = prevTxt };
            }
            return;
        }
        if (words.Count == 0) {
            segRaw = segRaw.TrimStart();
            if (segRaw.Length == 0) return;
        }
        if (words.Count > 0 && words[^1].EndSeconds <= words[^1].StartSeconds && wStart > words[^1].StartSeconds) {
            words[^1] = words[^1] with { EndSeconds = wStart };
        }
        words.Add(new LyricWordCue(wStart, wEnd, segRaw));
    }

    static void FinalizeWordCues(List<LyricWordCue> words, double? lineEndSec) {
        if (words.Count == 0) return;
        string firstTxt = words[0].Text.TrimStart();
        if (firstTxt.Length == 0) {
            words.RemoveAt(0);
            if (words.Count == 0) return;
        } else if (firstTxt != words[0].Text) {
            words[0] = words[0] with { Text = firstTxt };
        }

        string lastTxt = words[^1].Text.TrimEnd();
        if (lastTxt.Length == 0) {
            words.RemoveAt(words.Count - 1);
            if (words.Count == 0) return;
        } else if (lastTxt != words[^1].Text) {
            words[^1] = words[^1] with { Text = lastTxt };
        }

        for (int i = 0; i < words.Count; i++) {
            LyricWordCue w = words[i];
            double end = w.EndSeconds;
            if (end <= w.StartSeconds + 0.01) {
                if (i + 1 < words.Count && words[i + 1].StartSeconds > w.StartSeconds) {
                    end = words[i + 1].StartSeconds;
                } else if (lineEndSec.HasValue && lineEndSec.Value > w.StartSeconds) {
                    end = lineEndSec.Value;
                } else {
                    end = w.StartSeconds + Math.Max(0.22, w.Text.Trim().Length * 0.09);
                }
                words[i] = w with { EndSeconds = end };
            }
        }
    }

    static List<LyricWordCue> ShiftWordsForRepeat(List<LyricWordCue> words, double deltaSec) {
        if (words == null || words.Count == 0) return null;
        if (Math.Abs(deltaSec) <= 0.05) return words;
        List<LyricWordCue> shifted = new(words.Count);
        for (int i = 0; i < words.Count; i++) {
            LyricWordCue w = words[i];
            double s = Math.Max(0, w.StartSeconds + deltaSec);
            double e = Math.Max(s + 0.05, w.EndSeconds + deltaSec);
            shifted.Add(new LyricWordCue(s, e, w.Text));
        }
        return shifted;
    }

    static void InferTrailingLineAndWordEnds(List<LyricCue> cues) {
        for (int i = 0; i < cues.Count; i++) {
            LyricCue cue = cues[i];
            if (cue.TimeSeconds < 0) continue;
            double? nextTime = null;
            for (int j = i + 1; j < cues.Count; j++) {
                if (cues[j].TimeSeconds > cue.TimeSeconds + 0.05) {
                    nextTime = cues[j].TimeSeconds;
                    break;
                }
            }
            if (!cue.EndTimeSeconds.HasValue && nextTime.HasValue && !IsBreakText(cue.Text)) {
                double inferredEnd = Math.Min(nextTime.Value, cue.TimeSeconds + 8.0);
                if (inferredEnd > cue.TimeSeconds + 0.15) {
                    cue = cue with { EndTimeSeconds = inferredEnd };
                    cues[i] = cue;
                }
            }
            if (cue.Words is List<LyricWordCue> wordList && wordList.Count > 0) {
                LyricWordCue last = wordList[^1];
                if (cue.EndTimeSeconds.HasValue && cue.EndTimeSeconds.Value > last.StartSeconds &&
                    last.EndSeconds <= last.StartSeconds + 0.35) {
                    wordList[^1] = last with { EndSeconds = cue.EndTimeSeconds.Value };
                }
            }
        }
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

    sealed class TtmlSpanFrame(double? begin, double? end) {
        public double? Begin { get; } = begin;
        public double? End { get; } = end;
        public StringBuilder Text { get; } = new();
        public bool HasChildTimed { get; set; }
    }

    static void ExtractTimingAttrs(string attrs, out string beginVal, out string endVal, out string durVal) {
        beginVal = null;
        endVal = null;
        durVal = null;
        foreach (Match am in XmlAttrRegex().Matches(attrs)) {
            string key = am.Groups[1].Value;
            string val = am.Groups[2].Value;
            if (key.Equals("begin", StringComparison.OrdinalIgnoreCase)) beginVal = val;
            else if (key.Equals("end", StringComparison.OrdinalIgnoreCase)) endVal = val;
            else if (key.Equals("dur", StringComparison.OrdinalIgnoreCase)) durVal = val;
        }
    }

    static List<LyricCue> ParseTtmlCues(string normalized) {
        List<LyricCue> cues = [];
        foreach (Match m in TtmlLineRegex().Matches(normalized)) {
            ExtractTimingAttrs(m.Groups[1].Value, out string pBeginStr, out string pEndStr, out string pDurStr);
            if (!TryParseClockTimestamp(pBeginStr, out double start)) continue;
            double? end = null;
            if (TryParseClockTimestamp(pEndStr, out double e) && e > start) end = e;
            else if (TryParseClockTimestamp(pDurStr, out double d) && d > 0) end = start + d;

            string body = m.Groups[2].Value;
            List<LyricWordCue> words = [];
            StringBuilder plainBuf = new();
            Stack<TtmlSpanFrame> spanStack = new();
            bool pendingSpace = false;
            int cursor = 0;

            void AppendPlainText(string rawChunk) {
                if (string.IsNullOrEmpty(rawChunk)) return;
                string decoded = System.Net.WebUtility.HtmlDecode(rawChunk);
                if (spanStack.Count > 0) {
                    spanStack.Peek().Text.Append(decoded);
                } else {
                    plainBuf.Append(decoded);
                    if (words.Count > 0 && string.IsNullOrWhiteSpace(decoded)) {
                        pendingSpace = true;
                    }
                }
            }

            foreach (Match tok in TtmlTokenRegex().Matches(body)) {
                if (tok.Index > cursor) {
                    AppendPlainText(body[cursor..tok.Index]);
                }
                cursor = tok.Index + tok.Length;
                string tag = tok.Value;

                if (tag.StartsWith("<span", StringComparison.OrdinalIgnoreCase)) {
                    ExtractTimingAttrs(tag, out string sbStr, out string seStr, out string sdStr);
                    double? sBegin = null, sEnd = null;
                    if (TryParseClockTimestamp(sbStr, out double sb)) {
                        sBegin = sb;
                        if (TryParseClockTimestamp(seStr, out double se) && se > sb) sEnd = se;
                        else if (TryParseClockTimestamp(sdStr, out double sd) && sd > 0) sEnd = sb + sd;
                        foreach (TtmlSpanFrame parent in spanStack) {
                            parent.HasChildTimed = true;
                        }
                        if (spanStack.Count > 0 && spanStack.Peek().Text.Length > 0) {
                            string prefixTxt = spanStack.Peek().Text.ToString();
                            spanStack.Peek().Text.Clear();
                            plainBuf.Append(prefixTxt);
                            if (words.Count > 0 && string.IsNullOrWhiteSpace(prefixTxt)) pendingSpace = true;
                        }
                    }
                    spanStack.Push(new TtmlSpanFrame(sBegin, sEnd));
                } else if (tag.StartsWith("</span", StringComparison.OrdinalIgnoreCase)) {
                    if (spanStack.Count > 0) {
                        TtmlSpanFrame frame = spanStack.Pop();
                        string decoded = Regex.Replace(frame.Text.ToString(), @"[ \t\r\n]+", " ");
                        if (frame.Begin.HasValue && !frame.HasChildTimed) {
                            if (string.IsNullOrWhiteSpace(decoded)) {
                                if (words.Count > 0) pendingSpace = true;
                                plainBuf.Append(decoded);
                            } else {
                                if (pendingSpace && words.Count > 0 && !words[^1].Text.EndsWith(' ')) {
                                    words[^1] = words[^1] with { Text = words[^1].Text + " " };
                                    if (plainBuf.Length > 0 && !char.IsWhiteSpace(plainBuf[^1])) plainBuf.Append(' ');
                                }
                                pendingSpace = false;
                                if (words.Count == 0) decoded = decoded.TrimStart();
                                double wStart = Math.Max(0, frame.Begin.Value);
                                double wEnd = frame.End.HasValue && frame.End.Value > wStart ? frame.End.Value : wStart;
                                words.Add(new LyricWordCue(wStart, wEnd, decoded));
                                plainBuf.Append(decoded);
                            }
                        } else if (!string.IsNullOrEmpty(decoded)) {
                            if (spanStack.Count > 0) {
                                spanStack.Peek().Text.Append(decoded);
                            } else {
                                plainBuf.Append(decoded);
                                if (words.Count > 0 && string.IsNullOrWhiteSpace(decoded)) pendingSpace = true;
                            }
                        }
                    }
                } else if (tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) {
                    AppendPlainText(" ");
                }
            }

            if (cursor < body.Length) {
                AppendPlainText(body[cursor..]);
            }

            string lineText = Regex.Replace(plainBuf.ToString(), @"\s+", " ").Trim();
            if (words.Count > 0) {
                FinalizeWordCues(words, end);
            }
            if (!string.IsNullOrWhiteSpace(lineText)) {
                cues.Add(new LyricCue(start, lineText, end, words.Count > 0 ? words : null));
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
