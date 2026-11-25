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

    /// <summary>
    /// Serializes a <see cref="LyricsDocument"/> into standard LRC (or Enhanced LRC when syllable timings exist)
    /// for lossless container embedding during Studio/Classic audio export.
    /// </summary>
    public static string SerializeToLrc(LyricsDocument doc) {
        if (doc == null || !doc.HasLyrics) {
            return string.Empty;
        }
        if (doc.IsSynced && doc.Lines != null && doc.Lines.Count > 0) {
            StringBuilder sb = new(doc.Lines.Count * 48);
            for (int i = 0; i < doc.Lines.Count; i++) {
                LyricCue cue = doc.Lines[i];
                if (string.IsNullOrWhiteSpace(cue.Text)) {
                    continue;
                }
                sb.Append('[').Append(FormatLrcTimestamp(cue.TimeSeconds)).Append(']');
                if (doc.IsSyllableSynced && cue.Words != null && cue.Words.Count > 0) {
                    bool wordsCarrySpaces = cue.Words.Any(wd => !string.IsNullOrEmpty(wd.Text) && wd.Text.Contains(' '));
                    bool lineHasSpaces = cue.Text.Contains(' ');
                    for (int w = 0; w < cue.Words.Count; w++) {
                        LyricWordCue word = cue.Words[w];
                        if (string.IsNullOrEmpty(word.Text)) continue;
                        sb.Append('<').Append(FormatLrcTimestamp(word.StartSeconds)).Append('>').Append(word.Text);
                        if (!wordsCarrySpaces && lineHasSpaces && w + 1 < cue.Words.Count && !word.Text.EndsWith(' ')) {
                            sb.Append(' ');
                        }
                    }
                    LyricWordCue lastWord = cue.Words[^1];
                    if (lastWord.EndSeconds > lastWord.StartSeconds + 0.05) {
                        sb.Append('<').Append(FormatLrcTimestamp(lastWord.EndSeconds)).Append('>');
                    }
                } else {
                    sb.Append(cue.Text.Trim());
                }
                sb.Append('\n');
            }
            string built = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(built)) {
                return built;
            }
        }
        return (doc.RawUnsyncedText ?? string.Empty).Trim();
    }

    static string FormatLrcTimestamp(double seconds) {
        double safe = double.IsFinite(seconds) && seconds > 0 ? seconds : 0.0;
        int totalCentis = (int)Math.Round(safe * 100.0);
        int mins = totalCentis / 6000;
        int secs = (totalCentis / 100) % 60;
        int centis = totalCentis % 100;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:00}", mins, secs, centis);
    }

    const int MaxMoodDigestSections = 16;

    static readonly HashSet<string> HighArousalTokens = new(StringComparer.OrdinalIgnoreCase) {
        // EN
        "fire", "burn", "run", "fly", "rise", "shout", "scream", "thunder", "storm", "fight",
        "dance", "jump", "wild", "explode", "power", "free", "alive", "tonight", "higher", "fast",
        "rush", "break", "bang", "crash", "electric", "glow", "shine", "roar", "rage", "heart",
        // PT-BR / ES (accent-folded)
        "fogo", "queimar", "queima", "correr", "corre", "voar", "voa", "subir", "gritar", "grita",
        "trovao", "tempestade", "lutar", "luta", "dancar", "danca", "pular", "pula", "selvagem",
        "explodir", "poder", "forca", "livre", "vivo", "viva", "hoje", "rapido", "quebrar",
        "quebra", "eletrico", "brilhar", "brilha", "furia", "raiva", "coracao", "fuego", "volar",
        "bailar", "saltar", "fuerza", "tormenta", "latir"
    };

    static readonly HashSet<string> LowArousalTokens = new(StringComparer.OrdinalIgnoreCase) {
        // EN
        "sleep", "quiet", "silence", "soft", "slow", "whisper", "breathe", "calm", "still", "gentle",
        "drift", "fade", "shadow", "moon", "night", "rest", "peace", "dream", "float", "hush",
        "alone", "hollow", "cold", "rain", "mist", "memory", "sigh", "close", "stay", "hold",
        // PT-BR / ES (accent-folded)
        "dormir", "dorme", "quieto", "silencio", "suave", "devagar", "lento", "sussurro", "respirar",
        "respira", "calma", "calmo", "tranquilo", "sombra", "lua", "noite", "descanso", "paz",
        "sonho", "sonhar", "flutuar", "sozinho", "sozinha", "vazio", "frio", "chuva", "neblina",
        "memoria", "suspiro", "ficar", "fica", "abraco", "saudade", "sueno", "noche", "luna",
        "lluvia", "despacio", "susurro", "solo", "sola"
    };

    static readonly HashSet<string> PositiveValenceTokens = new(StringComparer.OrdinalIgnoreCase) {
        // EN
        "love", "light", "bright", "sun", "shine", "smile", "hope", "joy", "free", "alive",
        "fly", "rise", "dream", "home", "gold", "star", "heaven", "sweet", "together", "win",
        "happy", "grace", "kiss", "warm", "bloom", "magic", "dance", "glory", "forever", "strong",
        // PT-BR / ES (accent-folded)
        "amor", "amar", "amo", "luz", "brilho", "sol", "brilhar", "sorriso", "sorrir", "esperanca",
        "alegria", "feliz", "felicidade", "livre", "vivo", "voar", "sonho", "casa", "ouro", "estrela",
        "ceu", "doce", "juntos", "vencer", "graca", "beijo", "quente", "flor", "magia", "dancar",
        "gloria", "sempre", "forte", "paixao", "encanto", "esperanza", "estrella", "cielo", "beso", "sonrisa"
    };

    static readonly HashSet<string> NegativeValenceTokens = new(StringComparer.OrdinalIgnoreCase) {
        // EN
        "pain", "cry", "tears", "broken", "dark", "lost", "fear", "hate", "die", "dead",
        "bleed", "hurt", "cold", "alone", "empty", "fall", "shadow", "ghost", "ache", "sorrow",
        "burn", "scar", "lies", "gone", "goodbye", "nightmare", "crash", "trapped", "fade", "grief",
        // PT-BR / ES (accent-folded)
        "dor", "chorar", "choro", "lagrimas", "lagrima", "quebrado", "partido", "escuro", "escuridao",
        "perdido", "perdida", "medo", "odio", "morrer", "morto", "sangrar", "machucar", "ferida",
        "frio", "sozinho", "sozinha", "vazio", "cair", "sombra", "fantasma", "triste", "tristeza",
        "cicatriz", "mentira", "mentiras", "adeus", "pesadelo", "preso", "saudade", "luto", "solidao",
        "dolor", "llorar", "roto", "oscuro", "miedo", "morir", "soledad", "herida"
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
            string w = FoldDiacriticsLower(tok.ToString());
            tok.Clear();
            if (w.Length == 0) return;
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
                ttmlCues = StripLeadingTrailingCredits(ttmlCues);
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

            // NetEase YRC JSON credit header line: {"t":0,"c":[{"tx":"..."}]} -> never inject into synced playback cues
            if (line.StartsWith('{') && TryParseNeteaseCreditJsonLine(line, offsetSec, out _)) {
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

        // Stage A: Never inject NetEase YRC JSON credit headers (yrcCredits) into synced playback cues,
        // and strip deterministic multi-language leading/trailing credit legends cleanly.
        if (synced.Count >= 2 || (sawMsTimedLine && synced.Count >= 1)) {
            synced.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            synced = StripLeadingTrailingCredits(synced);
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

    // ---- W4: 2-Stage Synced Lyrics Credit Stripping & Fuzzy Plain-Lyrics Alignment ----

    static readonly string[] CjkCreditPrefixes = [
        "作词", "作曲", "编曲", "制作人", "词曲", "演唱", "原唱", "翻唱", "混音", "母带",
        "录音", "监制", "出品", "和声", "吉他", "贝斯", "键盘", "发行", "统筹", "企划",
        "后期", "混音师", "录音师", "母带工程师", "词", "曲", "鼓"
    ];

    static readonly string[] LatinCreditPrefixes = [
        "lyrics", "lyricist", "written by", "writer", "words by", "words",
        "music by", "music", "composed by", "composer", "produced by", "producer",
        "arranged by", "arrangement", "mixed by", "mixing", "mastered by", "mastering",
        "vocals by", "vocalist", "performed by", "singer", "artist", "recording",
        "engineered by", "engineer", "guitar", "bass", "drums", "piano", "keyboard",
        "copyright", "license", "synced by", "lrc by", "powered by",
        // PT-BR & ES
        "letra", "composicao", "compositor", "compositores", "producao", "produtor",
        "arranjo", "arranjos", "mixagem", "masterizacao", "interprete", "gravacao",
        "voz", "vocal", "edicao", "direcao", "musica por", "letra por", "escrita por",
        "produzido por", "compuesto por", "producido por", "mezcla", "masterizacion", "arreglos"
    ];

    static readonly string[] WatermarkSubstrings = [
        "qqmusic", "netease", "kugou", "musixmatch", "all rights reserved",
        "http://", "https://", "www.", "lrc by", "synced by"
    ];

    /// <summary>
    /// Stage-A deterministic classifier for non-lyric credit/metadata legends across EN, PT-BR, ES, and CJK.
    /// </summary>
    public static bool IsCreditLegendLine(string text, string trackTitle = null, string trackArtist = null) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }
        string trimmed = text.Trim();
        if (IsBreakText(trimmed)) {
            return false;
        }
        if (trimmed.Contains('©') || trimmed.Contains('℗')) {
            return true;
        }

        // Check CJK credit prefix labels (e.g. "作词 : ...", "编曲：...", "制作人: ...")
        for (int i = 0; i < CjkCreditPrefixes.Length; i++) {
            string pfx = CjkCreditPrefixes[i];
            if (trimmed.StartsWith(pfx, StringComparison.Ordinal)) {
                int rest = pfx.Length;
                while (rest < trimmed.Length && char.IsWhiteSpace(trimmed[rest])) rest++;
                if (rest < trimmed.Length && (trimmed[rest] == ':' || trimmed[rest] == '：' || trimmed[rest] == '-' || trimmed[rest] == '—')) {
                    return true;
                }
            }
        }

        string folded = FoldDiacriticsLower(trimmed);
        for (int i = 0; i < WatermarkSubstrings.Length; i++) {
            if (folded.Contains(WatermarkSubstrings[i], StringComparison.Ordinal)) {
                return true;
            }
        }

        // Check EN / PT-BR / ES credit labels ("Lyrics by ...", "Letra: ...", "Produção: ...", "Mix/Master: ...")
        if (folded.StartsWith("mix/master", StringComparison.Ordinal) ||
            folded.StartsWith("mix & master", StringComparison.Ordinal) ||
            folded.StartsWith("mix and master", StringComparison.Ordinal)) {
            return true;
        }
        for (int i = 0; i < LatinCreditPrefixes.Length; i++) {
            string pfx = LatinCreditPrefixes[i];
            if (folded.StartsWith(pfx, StringComparison.Ordinal)) {
                if (folded.Length == pfx.Length) {
                    continue;
                }
                int rest = pfx.Length;
                // Multi-word "by"/"por" prefixes match with just a following space
                if (pfx.EndsWith(" by", StringComparison.Ordinal) || pfx.EndsWith(" por", StringComparison.Ordinal)) {
                    if (char.IsWhiteSpace(folded[rest]) || folded[rest] == ':' || folded[rest] == '：') {
                        return true;
                    }
                    continue;
                }
                while (rest < folded.Length && char.IsWhiteSpace(folded[rest])) rest++;
                if (rest < folded.Length && (folded[rest] == ':' || folded[rest] == '：')) {
                    return true;
                }
            }
        }

        // Detect "Title - Artist" or "Artist - Title" header legend when track metadata is provided
        if ((!string.IsNullOrWhiteSpace(trackTitle) || !string.IsNullOrWhiteSpace(trackArtist)) &&
            (trimmed.Contains(" - ", StringComparison.Ordinal) || trimmed.Contains(" — ", StringComparison.Ordinal))) {
            string normTitle = FoldDiacriticsLower(trackTitle ?? "");
            string normArtist = FoldDiacriticsLower(trackArtist ?? "");
            bool hasTitle = normTitle.Length >= 2 && folded.Contains(normTitle, StringComparison.Ordinal);
            bool hasArtist = normArtist.Length >= 2 && folded.Contains(normArtist, StringComparison.Ordinal);
            if ((hasTitle && hasArtist) || (hasTitle && trimmed.Length <= (trackTitle?.Length ?? 0) + 32)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stage-A credit cleaner: strips leading/trailing credit legends without ever removing lyrics from the song body.
    /// </summary>
    public static List<LyricCue> StripLeadingTrailingCredits(
        IReadOnlyList<LyricCue> cues,
        string trackTitle = null,
        string trackArtist = null) {
        if (cues == null || cues.Count == 0) {
            return [];
        }
        List<LyricCue> working = new(cues);
        if (working.Count <= 1) {
            return working;
        }

        // 1. Scan leading metadata zone (up to first 16 cues; stop once 2 consecutive real lyric lines are seen)
        int scanLimit = Math.Min(16, working.Count);
        HashSet<int> removeIndices = [];
        int consecutiveReal = 0;
        int lastLeadingCreditIdx = -1;
        for (int i = 0; i < scanLimit; i++) {
            LyricCue c = working[i];
            if (IsBreakText(c.Text)) {
                continue;
            }
            if (IsCreditLegendLine(c.Text, trackTitle, trackArtist) ||
                (i == 0 && c.TimeSeconds <= 10.0 && LooksLikeTitleArtistHeader(c.Text))) {
                removeIndices.Add(i);
                lastLeadingCreditIdx = i;
                consecutiveReal = 0;
            } else {
                consecutiveReal++;
                if (consecutiveReal >= 2) {
                    break;
                }
            }
        }
        // Drop empty break markers that were strictly between removed leading credits
        if (lastLeadingCreditIdx >= 0) {
            for (int i = 0; i <= lastLeadingCreditIdx; i++) {
                if (IsBreakText(working[i].Text)) {
                    removeIndices.Add(i);
                }
            }
        }

        // 2. Scan trailing metadata zone (last 8 cues, walking backwards while credits/trailing breaks appear)
        int tailFloor = Math.Max(0, working.Count - 8);
        bool sawTailCredit = false;
        for (int i = working.Count - 1; i >= tailFloor; i--) {
            LyricCue c = working[i];
            if (IsBreakText(c.Text)) {
                if (sawTailCredit) {
                    removeIndices.Add(i);
                }
                continue;
            }
            if (IsCreditLegendLine(c.Text, trackTitle, trackArtist)) {
                removeIndices.Add(i);
                sawTailCredit = true;
            } else {
                break;
            }
        }

        if (removeIndices.Count == 0 || removeIndices.Count >= working.Count) {
            return working;
        }
        List<LyricCue> cleaned = new(working.Count - removeIndices.Count);
        for (int i = 0; i < working.Count; i++) {
            if (!removeIndices.Contains(i)) {
                cleaned.Add(working[i]);
            }
        }
        // Trim any leading break cue left behind right after removed credits
        while (cleaned.Count > 1 && IsBreakText(cleaned[0].Text) && lastLeadingCreditIdx >= 0) {
            cleaned.RemoveAt(0);
        }
        return cleaned.Count > 0 ? cleaned : working;
    }

    /// <summary>
    /// Stage-A + Stage-B cleaner: applies deterministic credit stripping and then aligns synced cues against a
    /// clean plain-lyrics reference using accent-folded token Dice similarity + edit-distance-1 matching.
    /// Strict safety guard: scans prefix strictly in <c>k in [1..min(12, count-2)]</c> requiring either
    /// 2 consecutive matches <c>&gt;= 0.55</c> OR 1 high-confidence match <c>&gt;= 0.78</c> with <c>&gt;= 3</c> words.
    /// </summary>
    public static LyricsDocument AlignAndCleanWithPlainLyrics(
        LyricsDocument syncedDoc,
        string plainLyrics,
        string trackTitle = null,
        string trackArtist = null) {
        if (syncedDoc == null || !syncedDoc.IsSynced || syncedDoc.Lines == null || syncedDoc.Lines.Count == 0) {
            return syncedDoc ?? new LyricsDocument();
        }

        List<LyricCue> cues = StripLeadingTrailingCredits(syncedDoc.Lines, trackTitle, trackArtist);

        if (!string.IsNullOrWhiteSpace(plainLyrics) && cues.Count >= 3) {
            List<string> plainLines = ExtractCleanPlainLines(plainLyrics, trackTitle, trackArtist);
            if (plainLines.Count >= 2) {
                // Find first non-break cue index
                int firstLyricIdx = 0;
                while (firstLyricIdx < cues.Count && IsBreakText(cues[firstLyricIdx].Text)) {
                    firstLyricIdx++;
                }

                // Only attempt prefix trimming if cue[firstLyricIdx] does NOT already match plainLines[0]
                if (firstLyricIdx < cues.Count &&
                    ComputeFuzzyLineSimilarity(cues[firstLyricIdx].Text, plainLines[0]) < 0.55) {
                    int maxK = Math.Min(12, cues.Count - 2);
                    int matchedStart = -1;
                    for (int k = Math.Max(1, firstLyricIdx + 1); k <= maxK; k++) {
                        if (IsBreakText(cues[k].Text)) {
                            continue;
                        }
                        double sim0 = ComputeFuzzyLineSimilarity(cues[k].Text, plainLines[0]);
                        if (sim0 < 0.55) {
                            continue;
                        }
                        bool highConfSingle = sim0 >= 0.78 && CountWords(plainLines[0]) >= 3;
                        bool twoConsecutive = false;
                        int nextNonBreak = k + 1;
                        while (nextNonBreak < cues.Count && IsBreakText(cues[nextNonBreak].Text)) {
                            nextNonBreak++;
                        }
                        if (nextNonBreak < cues.Count &&
                            ComputeFuzzyLineSimilarity(cues[nextNonBreak].Text, plainLines[1]) >= 0.55) {
                            twoConsecutive = true;
                        }
                        if (twoConsecutive || highConfSingle) {
                            // Verify no skipped prefix line strongly matches any plain lyric line
                            bool wouldDropRealLyric = false;
                            for (int p = 0; p < k; p++) {
                                if (IsBreakText(cues[p].Text) || IsCreditLegendLine(cues[p].Text, trackTitle, trackArtist)) {
                                    continue;
                                }
                                int checkRange = Math.Min(plainLines.Count, 8);
                                for (int pl = 0; pl < checkRange; pl++) {
                                    if (ComputeFuzzyLineSimilarity(cues[p].Text, plainLines[pl]) >= 0.65) {
                                        wouldDropRealLyric = true;
                                        break;
                                    }
                                }
                                if (wouldDropRealLyric) break;
                            }
                            if (!wouldDropRealLyric) {
                                matchedStart = k;
                            }
                            break;
                        }
                    }

                    if (matchedStart > 0 && matchedStart < cues.Count) {
                        cues = cues.GetRange(matchedStart, cues.Count - matchedStart);
                    }
                }

                // Trailing junk check (last <= 6 cues after verified final plain line)
                if (cues.Count >= 3 && plainLines.Count >= 2) {
                    string lastPlain = plainLines[^1];
                    int minTailIdx = Math.Max(1, cues.Count - 6);
                    int lastMatchedCue = -1;
                    for (int m = cues.Count - 1; m >= minTailIdx; m--) {
                        if (IsBreakText(cues[m].Text)) continue;
                        if (ComputeFuzzyLineSimilarity(cues[m].Text, lastPlain) >= 0.65) {
                            lastMatchedCue = m;
                            break;
                        }
                    }
                    if (lastMatchedCue >= 0 && lastMatchedCue < cues.Count - 1) {
                        bool tailHasRealLyric = false;
                        for (int t = lastMatchedCue + 1; t < cues.Count; t++) {
                            if (IsBreakText(cues[t].Text) || IsCreditLegendLine(cues[t].Text, trackTitle, trackArtist)) {
                                continue;
                            }
                            int tailPlainStart = Math.Max(0, plainLines.Count - 6);
                            for (int pl = tailPlainStart; pl < plainLines.Count; pl++) {
                                if (ComputeFuzzyLineSimilarity(cues[t].Text, plainLines[pl]) >= 0.55) {
                                    tailHasRealLyric = true;
                                    break;
                                }
                            }
                            if (tailHasRealLyric) break;
                        }
                        if (!tailHasRealLyric) {
                            cues = cues.GetRange(0, lastMatchedCue + 1);
                        }
                    }
                }
            }
        }

        InferTrailingLineAndWordEnds(cues);
        return new LyricsDocument {
            IsSynced = true,
            IsSyllableSynced = cues.Any(c => c.Words is { Count: > 0 }),
            Lines = cues,
            RawUnsyncedText = string.Join("\n", cues.Select(c => c.Text)),
            SourceLabel = syncedDoc.SourceLabel
        };
    }

    /// <summary>
    /// Computes accent-folded token Sørensen–Dice similarity in [0..1] allowing Levenshtein edit distance &lt;= 1
    /// on tokens of length &gt;= 4 (tolerating minor punctuation, accent, or spelling variations across providers).
    /// </summary>
    public static double ComputeFuzzyLineSimilarity(string a, string b) {
        List<string> tokA = TokenizeForLyricsAlign(a);
        List<string> tokB = TokenizeForLyricsAlign(b);
        if (tokA.Count == 0 || tokB.Count == 0) {
            return 0.0;
        }
        bool[] usedB = new bool[tokB.Count];
        int matches = 0;
        for (int i = 0; i < tokA.Count; i++) {
            string wa = tokA[i];
            for (int j = 0; j < tokB.Count; j++) {
                if (usedB[j]) continue;
                string wb = tokB[j];
                if (string.Equals(wa, wb, StringComparison.Ordinal) ||
                    (wa.Length >= 4 && wb.Length >= 4 && IsEditDistanceAtMostOne(wa, wb))) {
                    usedB[j] = true;
                    matches++;
                    break;
                }
            }
        }
        return (2.0 * matches) / (tokA.Count + tokB.Count);
    }

    static bool LooksLikeTitleArtistHeader(string text) {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 72) {
            return false;
        }
        int sep = text.IndexOf(" - ", StringComparison.Ordinal);
        if (sep <= 0 || sep >= text.Length - 3) {
            return false;
        }
        // Only treat concise "Song - Artist" headers with <= 4 words per side and no lyric punctuation
        string left = text[..sep].Trim();
        string right = text[(sep + 3)..].Trim();
        return !text.Contains('!') && !text.Contains('?') && !text.Contains(',') &&
               CountWords(left) is >= 1 and <= 4 && CountWords(right) is >= 1 and <= 4;
    }

    static List<string> ExtractCleanPlainLines(string plainLyrics, string trackTitle, string trackArtist) {
        string[] raw = plainLyrics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<string> clean = new(raw.Length);
        for (int i = 0; i < raw.Length; i++) {
            string line = raw[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || IsBreakText(line)) {
                continue;
            }
            // Skip structural section tags like "[Verse 1]", "[Chorus]", "(Intro)"
            if ((line.StartsWith('[') && line.EndsWith(']') && line.Length <= 32) ||
                (line.StartsWith('(') && line.EndsWith(')') && line.Length <= 24)) {
                continue;
            }
            if (IsCreditLegendLine(line, trackTitle, trackArtist)) {
                continue;
            }
            clean.Add(line);
        }
        return clean;
    }

    static List<string> TokenizeForLyricsAlign(string text) {
        List<string> tokens = [];
        if (string.IsNullOrWhiteSpace(text)) {
            return tokens;
        }
        string folded = FoldDiacriticsLower(text);
        StringBuilder cur = new(16);
        for (int i = 0; i < folded.Length; i++) {
            char c = folded[i];
            if (char.IsLetterOrDigit(c)) {
                // Treat CJK ideographs individually so unspaced Chinese/Japanese/Korean lines tokenize cleanly
                if (c >= '\u4E00' && c <= '\u9FFF') {
                    if (cur.Length > 0) {
                        tokens.Add(cur.ToString());
                        cur.Clear();
                    }
                    tokens.Add(c.ToString());
                } else {
                    cur.Append(c);
                }
            } else if (cur.Length > 0) {
                tokens.Add(cur.ToString());
                cur.Clear();
            }
        }
        if (cur.Length > 0) {
            tokens.Add(cur.ToString());
        }
        return tokens;
    }

    static int CountWords(string text) => TokenizeForLyricsAlign(text).Count;

    static bool IsEditDistanceAtMostOne(string a, string b) {
        int diff = a.Length - b.Length;
        if (diff > 1 || diff < -1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length) {
            if (a[i] == b[j]) {
                i++;
                j++;
            } else {
                if (++edits > 1) return false;
                if (a.Length > b.Length) i++;
                else if (b.Length > a.Length) j++;
                else { i++; j++; }
            }
        }
        if (i < a.Length || j < b.Length) edits++;
        return edits <= 1;
    }

    static string FoldDiacriticsLower(string input) {
        if (string.IsNullOrEmpty(input)) {
            return string.Empty;
        }
        string formD = input.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(formD.Length);
        for (int i = 0; i < formD.Length; i++) {
            char c = formD[i];
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) {
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
