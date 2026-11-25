using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Android.Content;
using Android.Graphics;

using CavernizeAndroid.Language;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class OnlineCoverCandidate {
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Source { get; init; } = "";
    public string ImageUrl { get; init; } = "";
    public int SourceRank { get; init; } = 99;
    public int MatchScore { get; init; }
    public bool IsExactMatch { get; init; }
}

public sealed class OnlineLyricsCandidate {
    public string TrackName { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public string Source { get; init; } = "LRCLIB";
    public bool IsSynced { get; init; }
    public bool IsSyllableSynced { get; init; }
    public string SyncedLrc { get; init; } = "";
    public string PlainLyrics { get; init; } = "";
    public double DurationSec { get; init; }
    public int SourceRank { get; init; } = 99;
    public int MatchScore { get; init; }
    public int MatchRiskScore { get; init; }
    public string MatchRiskNotes { get; init; } = "";
    public bool IsExactMatch { get; init; }
    public string BestContent => !string.IsNullOrWhiteSpace(SyncedLrc) ? SyncedLrc : PlainLyrics;
}

public sealed class OnlineSubtitleCandidate {
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Language { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Source { get; init; } = "";
    public int SourceRank { get; init; } = 99;
    public int MatchScore { get; init; }
    public bool IsExactMatch { get; init; }
}

public sealed class OnlineEnrichResult {
    public Bitmap ArtworkBitmap { get; init; }
    public LyricsDocument Lyrics { get; init; }
    public string ArtworkSource { get; init; } = "";
    public string LyricsSource { get; init; } = "";
}

/// <summary>
/// Multi-provider online metadata, album art, synced lyrics, and subtitle fetcher inspired by
/// Kodi's metadata scrapers (MusicBrainz/CoverArtArchive, TheAudioDB, TMDb) and Poweramp (iTunes/Web + LRCLIB).
/// Caches all fetched assets inside private app storage for instant offline reuse.
/// </summary>
public static partial class OnlineMetadataService {
    const string AudioDbKey = "95424d43204d6564696538"; // Kodi metadata.generic.albums
    const string TmdbApiKey = "f090bb54758cabf231fb605d3e3e0468"; // Kodi metadata.themoviedb.org.python
    const string AppUserAgent = "CavernizeAndroid/2.9 (https://github.com/guumachadooo/Cavern)";

    static readonly string[] ImageCacheExtensions = [".jpg", ".png", ".webp"];
    static readonly SemaphoreSlim musicBrainzRateLock = new(1, 1);
    static long lastMusicBrainzRequestMs;
    static readonly HttpClient http = CreateHttpClient();

    const string TechNoiseTerms =
        @"(?:\d+\s*bit|\d+(?:\.\d+)?\s*k?hz|flac|alac|aac|eac3|e-ac-3|ac3|dts(?:-?hd)?|atmos|dolby|joc|truehd|qobuz|tidal|deezer|web-?dl|webrip|bdrip|bluray|remux|mvsep|spatial|binaural|\d+\s*kbps|\d+\s*kbits|\d+ch|lossless|hi-?res|mqa|mp3|ogg|opus|wav|dsd\d*|cdrip|uhd|4k|1080p|720p|2160p|official\s+(?:audio|video|music\s+video|lyric\s+video|visualizer)|lyric\s+video)";

    [GeneratedRegex(@"(?:\[[^\]]*\b" + TechNoiseTerms + @"\b[^\]]*\]|\([^\)]*\b" + TechNoiseTerms + @"\b[^\)]*\)|[\[\(]\s*(?:explicit|clean(?:\s+version)?)\s*[\]\)])", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"\[[^\]]*\b" + TechNoiseTerms + @"\b[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TechnicalBracketRegex();

    [GeneratedRegex(@"[\[\(].*?[\]\)]", RegexOptions.Compiled)]
    private static partial Regex AllBracketsRegex();

    [GeneratedRegex(@"\s+-\s+(?:live|remix|acoustic|instrumental|radio\s+edit|slowed|sped\s+up|unplugged|remaster(?:ed)?|karaoke|demo|edit)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TrailingEditionDashRegex();

    [GeneratedRegex(@"^\d{1,3}\s*[-._]\s*", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackNumRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(14) };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    static string GetCacheSubdir(Context ctx, string sub) {
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        string[] roots = [
            effectiveCtx?.FilesDir?.AbsolutePath,
            effectiveCtx?.CacheDir?.AbsolutePath,
            IOPath.GetTempPath()
        ];
        foreach (string root in roots) {
            if (string.IsNullOrEmpty(root)) {
                continue;
            }
            try {
                string dir = IOPath.Combine(root, "online_cache", sub);
                Directory.CreateDirectory(dir);
                return dir;
            } catch {
                // Fall back to next app-private storage root
            }
        }
        return null;
    }

    static string HashKey(string raw) {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes((raw ?? "").Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash)[..24];
    }

    public static string CleanSearchTerm(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Replace('_', ' ');
        s = LeadingTrackNumRegex().Replace(s, "");
        s = BracketNoiseRegex().Replace(s, " ");
        s = MultiSpaceRegex().Replace(s, " ").Trim();
        return s;
    }

    /// <summary>
    /// Cleans technical rip/codec tags while PRESERVING edition qualifiers like (Deluxe),
    /// (The Til Dawn Edition), (Taylor's Version), (3am Edition), (Expanded Edition), etc.
    /// </summary>
    public static string CleanAlbumForSearch(string album) {
        if (string.IsNullOrWhiteSpace(album)) return "";
        string s = album.Replace('_', ' ');
        s = TechnicalBracketRegex().Replace(s, " ");
        s = MultiSpaceRegex().Replace(s, " ").Trim();
        return s;
    }

    static string StripAllBrackets(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Replace('_', ' ');
        s = LeadingTrackNumRegex().Replace(s, "");
        s = AllBracketsRegex().Replace(s, " ");
        return MultiSpaceRegex().Replace(s, " ").Trim();
    }

    static bool IsGenericAlbumName(string album) {
        if (string.IsNullOrWhiteSpace(album)) return true;
        return album.Equals("Download", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Music", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Audiobooks", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeCompare(string raw) {
        string clean = CleanSearchTerm(raw);
        return NormalizeAlnumSpaces(clean);
    }

    static string NormalizeCompareFull(string raw) {
        string clean = CleanAlbumForSearch(raw);
        return NormalizeAlnumSpaces(clean);
    }

    static string NormalizeAlnumSpaces(string clean) {
        if (string.IsNullOrEmpty(clean)) return "";
        string decomposed = clean.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(decomposed.Length);
        foreach (char c in decomposed) {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else sb.Append(' ');
        }
        return MultiSpaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    static bool ArtistExactMatch(string candArtist, string targetArtist) {
        string normTarget = NormalizeCompare(targetArtist);
        if (string.IsNullOrEmpty(normTarget)) return false;
        string[] parts = (candArtist ?? "").Split(['/', ',', '&', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string p in parts) {
            if (NormalizeCompare(p) == normTarget) return true;
        }
        return NormalizeCompare(candArtist) == normTarget;
    }

    static string FindExistingCachedImage(string dir, string baseHash) {
        if (string.IsNullOrEmpty(dir)) return null;
        for (int i = 0; i < ImageCacheExtensions.Length; i++) {
            string candidate = IOPath.Combine(dir, baseHash + ImageCacheExtensions[i]);
            if (IOFile.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string GetManualCoverOverridePath(Context ctx, string filePath) {
        if (ctx == null || string.IsNullOrWhiteSpace(filePath)) return null;
        string dir = GetCacheSubdir(ctx, "cover_overrides");
        if (string.IsNullOrEmpty(dir)) return null;
        string match = FindExistingCachedImage(dir, HashKey($"file|{filePath}"));
        if (!string.IsNullOrEmpty(match)) return match;

        if (filePath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) {
            try {
                string direct = AndroidFileHelper.TryGetDirectPath(ctx, Android.Net.Uri.Parse(filePath));
                if (!string.IsNullOrEmpty(direct)) {
                    return FindExistingCachedImage(dir, HashKey($"file|{direct}"));
                }
            } catch {
                // Ignore invalid URI
            }
        } else if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
            try {
                string local = Android.Net.Uri.Parse(filePath)?.Path;
                if (!string.IsNullOrEmpty(local)) {
                    return FindExistingCachedImage(dir, HashKey($"file|{local}"));
                }
            } catch {
                // Ignore invalid URI
            }
        }
        return null;
    }

    public static string GetCachedCoverPath(Context ctx, string title, string artist, string album, string filePath = null) {
        if (ctx == null) return null;
        string ovr = GetManualCoverOverridePath(ctx, filePath);
        if (!string.IsNullOrEmpty(ovr)) return ovr;

        string dir = GetCacheSubdir(ctx, "covers");
        if (string.IsNullOrEmpty(dir)) return null;
        string cleanAlb = IsGenericAlbumName(album) ? "" : album.Trim();
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrEmpty(cleanAlb)) {
            string pEd = FindExistingCachedImage(dir, HashKey($"trk|{artist}|{cleanAlb}|{title}"));
            if (!string.IsNullOrEmpty(pEd)) return pEd;
        }
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrEmpty(cleanAlb)) {
            string pAlb = FindExistingCachedImage(dir, HashKey($"alb|{artist}|{cleanAlb}"));
            if (!string.IsNullOrEmpty(pAlb)) return pAlb;
        }
        if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrEmpty(cleanAlb)) {
            string pTrk = FindExistingCachedImage(dir, HashKey($"trk|{artist}|{title}"));
            if (!string.IsNullOrEmpty(pTrk)) return pTrk;
        }
        return null;
    }

    public static string GetCachedLyricsPath(Context ctx, string title, string artist) {
        if (ctx == null || string.IsNullOrWhiteSpace(title)) return null;
        string dir = GetCacheSubdir(ctx, "lyrics");
        if (string.IsNullOrEmpty(dir)) return null;
        string hash = HashKey($"lyr|{artist}|{title}");
        foreach (string ext in new[] { ".elrc", ".ttml", ".krc", ".lrc" }) {
            string p = IOPath.Combine(dir, hash + ext);
            if (IOFile.Exists(p)) return p;
        }
        return null;
    }

    public static string SaveLyricsToCache(Context ctx, string title, string artist, string content, bool onlyIfUpgrade = false) {
        if (ctx == null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) return null;
        string dir = GetCacheSubdir(ctx, "lyrics");
        if (string.IsNullOrEmpty(dir)) return null;
        string p = IOPath.Combine(dir, HashKey($"lyr|{artist}|{title}") + ".lrc");
        try {
            if (onlyIfUpgrade) {
                string existingPath = GetCachedLyricsPath(ctx, title, artist);
                if (!string.IsNullOrEmpty(existingPath) && IOFile.Exists(existingPath)) {
                    string existingRaw = existingPath.EndsWith(".krc", StringComparison.OrdinalIgnoreCase)
                        ? LyricsService.TryDecodeKrcBytes(IOFile.ReadAllBytes(existingPath))
                        : MediaServiceUtils.ReadFileTextAutoEncoding(existingPath);
                    int existingTier = LyricsService.GetSyncTier(LyricsService.ParseLyricsText(existingRaw, "Online Cache"));
                    int newTier = LyricsService.GetSyncTier(LyricsService.ParseLyricsText(content, "Online Cache"));
                    if (newTier <= existingTier) {
                        return existingPath;
                    }
                }
            }
            MediaServiceUtils.WriteTextAtomic(p, content);
            return p;
        } catch {
            return null;
        }
    }

    public static async Task<List<OnlineCoverCandidate>> SearchCoverCandidatesAsync(
        string query,
        string artist,
        string album,
        bool isVideo,
        CancellationToken ct = default) {
        List<OnlineCoverCandidate> raw = [];
        string cleanQuery = CleanSearchTerm(query);
        string cleanArtist = CleanSearchTerm(artist);
        string fullAlbum = IsGenericAlbumName(album) ? "" : CleanAlbumForSearch(album);
        string baseAlbum = IsGenericAlbumName(album) ? "" : StripAllBrackets(album);
        if (string.IsNullOrWhiteSpace(cleanQuery) && string.IsNullOrWhiteSpace(fullAlbum)) {
            return raw;
        }

        if (isVideo) {
            await TrySearchTmdbAsync(cleanQuery, raw, ct).ConfigureAwait(false);
        }

        if (!isVideo && !string.IsNullOrWhiteSpace(fullAlbum)) {
            // Search exact album (preserving Deluxe / Edition / Anthology qualifiers!) first
            string albumQuery = string.IsNullOrWhiteSpace(cleanArtist) ? fullAlbum : $"{cleanArtist} {fullAlbum}";
            await TrySearchItunesAsync(albumQuery, "album", 8, raw, ct).ConfigureAwait(false);
        }

        string songQuery = !string.IsNullOrWhiteSpace(fullAlbum)
            ? $"{cleanArtist} {cleanQuery} {fullAlbum}".Trim()
            : (string.IsNullOrWhiteSpace(cleanArtist) ? cleanQuery : $"{cleanArtist} {cleanQuery}");
        await TrySearchItunesAsync(songQuery, "song", 8, raw, ct).ConfigureAwait(false);

        // If including the full edition name produced too few iTunes results, query artist+song as secondary fallback
        if (raw.Count < 2 && !string.IsNullOrWhiteSpace(cleanQuery)) {
            string fbTerm = string.IsNullOrWhiteSpace(cleanArtist) ? cleanQuery : $"{cleanArtist} {cleanQuery}";
            if (!string.Equals(fbTerm, songQuery, StringComparison.OrdinalIgnoreCase)) {
                await TrySearchItunesAsync(fbTerm, "song", 6, raw, ct).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(fullAlbum)) {
            await TrySearchAudioDbAsync(cleanArtist, fullAlbum, raw, ct).ConfigureAwait(false);
            await TrySearchMusicBrainzAsync(cleanArtist, fullAlbum, raw, ct).ConfigureAwait(false);
        }

        string normTitle = NormalizeCompare(cleanQuery);
        string normArtist = NormalizeCompare(cleanArtist);
        string normFullAlbum = NormalizeCompareFull(fullAlbum);
        string normBaseAlbum = NormalizeAlnumSpaces(baseAlbum);
        bool hasEditionQualifier = !string.IsNullOrEmpty(normFullAlbum) && normFullAlbum != normBaseAlbum;

        List<OnlineCoverCandidate> ranked = raw
            .GroupBy(r => r.ImageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(c => {
                int rank = c.Source switch {
                    "TMDb Poster" => isVideo ? 1 : 5,
                    "iTunes HD" => isVideo ? 2 : 1,
                    "TheAudioDB" => 2,
                    "CoverArtArchive" => 3,
                    _ => 4
                };
                string cTitle = NormalizeCompare(c.Title);
                string cFullAlbum = NormalizeCompareFull(c.Album);
                string cBaseAlbum = NormalizeAlnumSpaces(StripAllBrackets(c.Album));

                bool titleMatch = !string.IsNullOrEmpty(normTitle) && cTitle == normTitle;
                bool fullAlbumMatch = !string.IsNullOrEmpty(normFullAlbum) &&
                    (cFullAlbum == normFullAlbum || NormalizeCompareFull(c.Title) == normFullAlbum);
                bool baseAlbumMatch = !string.IsNullOrEmpty(normBaseAlbum) &&
                    (cBaseAlbum == normBaseAlbum || cTitle == normBaseAlbum);
                bool editionMismatch = hasEditionQualifier && !fullAlbumMatch && baseAlbumMatch;
                bool artistMatch = !string.IsNullOrEmpty(normArtist) && ArtistExactMatch(c.Artist, cleanArtist);

                int score = (titleMatch ? 450 : (!string.IsNullOrEmpty(normTitle) && cTitle.Contains(normTitle) ? 180 : 0))
                    + (artistMatch ? 380 : 0)
                    + (fullAlbumMatch ? 650 : (baseAlbumMatch ? (editionMismatch ? -220 : 180) : 0))
                    - (rank - 1) * 35;

                bool exact = isVideo
                    ? titleMatch
                    : (!string.IsNullOrEmpty(normFullAlbum)
                        ? artistMatch && fullAlbumMatch && (titleMatch || string.IsNullOrEmpty(cTitle) || cFullAlbum == normFullAlbum)
                        : artistMatch && (titleMatch || baseAlbumMatch));

                return new OnlineCoverCandidate {
                    Title = c.Title,
                    Artist = c.Artist,
                    Album = c.Album,
                    Source = c.Source,
                    ImageUrl = c.ImageUrl,
                    SourceRank = rank,
                    MatchScore = score,
                    IsExactMatch = exact
                };
            })
            .OrderByDescending(c => c.IsExactMatch)
            .ThenByDescending(c => c.MatchScore)
            .ThenBy(c => c.SourceRank)
            .Take(12)
            .ToList();

        return ranked;
    }

    static async Task TrySearchItunesAsync(string term, string entity, int limit, List<OnlineCoverCandidate> results, CancellationToken ct) {
        try {
            string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity={entity}&limit={limit}";
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement item in arr.EnumerateArray()) {
                string art100 = item.TryGetProperty("artworkUrl100", out JsonElement aEl) ? (aEl.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(art100)) continue;
                string hiRes = art100.Replace("100x100bb", "1000x1000bb");
                string trackName = item.TryGetProperty("trackName", out JsonElement tEl) ? (tEl.GetString() ?? "") : "";
                string albumName = item.TryGetProperty("collectionName", out JsonElement alEl) ? (alEl.GetString() ?? "") : "";
                results.Add(new OnlineCoverCandidate {
                    Title = !string.IsNullOrWhiteSpace(trackName) ? trackName : albumName,
                    Artist = item.TryGetProperty("artistName", out JsonElement arEl) ? (arEl.GetString() ?? "") : "",
                    Album = albumName,
                    Source = "iTunes HD",
                    ImageUrl = hiRes
                });
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.iTunes: {ex.Message}");
        }
    }

    static async Task TrySearchAudioDbAsync(string artist, string album, List<OnlineCoverCandidate> results, CancellationToken ct) {
        try {
            string url = $"https://www.theaudiodb.com/api/v1/json/{AudioDbKey}/searchalbum.php?s={Uri.EscapeDataString(artist)}&a={Uri.EscapeDataString(album)}";
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("album", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement item in arr.EnumerateArray()) {
                string thumb = item.TryGetProperty("strAlbumThumb", out JsonElement tEl) ? (tEl.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(thumb)) continue;
                results.Add(new OnlineCoverCandidate {
                    Title = item.TryGetProperty("strAlbum", out JsonElement nEl) ? (nEl.GetString() ?? "") : album,
                    Artist = item.TryGetProperty("strArtist", out JsonElement aEl) ? (aEl.GetString() ?? "") : artist,
                    Album = item.TryGetProperty("strAlbum", out JsonElement alEl) ? (alEl.GetString() ?? "") : album,
                    Source = "TheAudioDB",
                    ImageUrl = thumb
                });
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.TheAudioDB: {ex.Message}");
        }
    }

    static async Task TrySearchMusicBrainzAsync(string artist, string album, List<OnlineCoverCandidate> results, CancellationToken ct) {
        try {
            string q = $"release:\"{album}\" AND artist:\"{artist}\"";
            string url = $"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(q)}&fmt=json&limit=3";

            string json = null;
            await musicBrainzRateLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                long elapsed = Environment.TickCount64 - lastMusicBrainzRequestMs;
                if (lastMusicBrainzRequestMs > 0 && elapsed < 1050) {
                    await Task.Delay((int)(1050 - elapsed), ct).ConfigureAwait(false);
                }
                lastMusicBrainzRequestMs = Environment.TickCount64;

                using HttpRequestMessage req = new(HttpMethod.Get, url);
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd(AppUserAgent);
                using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            } finally {
                musicBrainzRateLock.Release();
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("releases", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement rel in arr.EnumerateArray()) {
                string mbid = rel.TryGetProperty("id", out JsonElement idEl) ? (idEl.GetString() ?? "") : "";
                string relTitle = rel.TryGetProperty("title", out JsonElement tEl) ? (tEl.GetString() ?? album) : album;
                if (string.IsNullOrEmpty(mbid)) continue;
                results.Add(new OnlineCoverCandidate {
                    Title = relTitle,
                    Artist = artist,
                    Album = relTitle,
                    Source = "CoverArtArchive",
                    ImageUrl = $"https://coverartarchive.org/release/{mbid}/front-500"
                });
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.MusicBrainz: {ex.Message}");
        }
    }

    static async Task TrySearchTmdbAsync(string titleQuery, List<OnlineCoverCandidate> results, CancellationToken ct) {
        try {
            string tmdbLang = Uri.EscapeDataString(AndroidStrings.Active["TmdbLanguageCode"]);
            string url = $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&query={Uri.EscapeDataString(titleQuery)}&language={tmdbLang}";
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
            int count = 0;
            foreach (JsonElement item in arr.EnumerateArray()) {
                if (count >= 4) break;
                string poster = item.TryGetProperty("poster_path", out JsonElement pEl) ? (pEl.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(poster)) continue;
                string mvTitle = item.TryGetProperty("title", out JsonElement tEl) ? (tEl.GetString() ?? "") : titleQuery;
                string relDate = item.TryGetProperty("release_date", out JsonElement dEl) ? (dEl.GetString() ?? "") : "";
                results.Add(new OnlineCoverCandidate {
                    Title = mvTitle,
                    Artist = relDate.Length >= 4 ? relDate[..4] : "TMDb",
                    Album = AndroidStrings.Active["MovieVideoLabel"],
                    Source = "TMDb Poster",
                    ImageUrl = $"https://image.tmdb.org/t/p/w780{poster}"
                });
                count++;
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.TMDb: {ex.Message}");
        }
    }

    public static async Task<(Bitmap bitmap, string savedPath)> DownloadAndCacheCoverAsync(
        Context ctx,
        string imageUrl,
        string title,
        string artist,
        string album,
        CancellationToken ct = default,
        string filePathOverride = null) {
        if (string.IsNullOrWhiteSpace(imageUrl)) return (null, null);
        try {
            using HttpResponseMessage resp = await http.GetAsync(imageUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && imageUrl.Contains("1000x1000bb")) {
                string fallbackUrl = imageUrl.Replace("1000x1000bb", "600x600bb");
                using HttpResponseMessage fbResp = await http.GetAsync(fallbackUrl, ct).ConfigureAwait(false);
                if (!fbResp.IsSuccessStatusCode) return (null, null);
                byte[] fbBytes = await fbResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return SaveAndDecodeCoverBytes(ctx, fbBytes, title, artist, album, filePathOverride);
            }
            if (!resp.IsSuccessStatusCode) return (null, null);
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return SaveAndDecodeCoverBytes(ctx, bytes, title, artist, album, filePathOverride);
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.DownloadCover: {ex.Message}");
            return (null, null);
        }
    }

    static string DetectImageExtension(byte[] bytes) {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) {
            return ".png";
        }
        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') {
            return ".webp";
        }
        return ".jpg";
    }

    static string WriteCachedImageAtomic(string dir, string baseHash, string ext, byte[] bytes) {
        for (int i = 0; i < ImageCacheExtensions.Length; i++) {
            string staleExt = ImageCacheExtensions[i];
            if (staleExt == ext) continue;
            string stale = IOPath.Combine(dir, baseHash + staleExt);
            try {
                if (IOFile.Exists(stale)) IOFile.Delete(stale);
            } catch { }
        }
        string target = IOPath.Combine(dir, baseHash + ext);
        MediaServiceUtils.WriteBytesAtomic(target, bytes);
        return target;
    }

    static (Bitmap bitmap, string savedPath) SaveAndDecodeCoverBytes(
        Context ctx, byte[] bytes, string title, string artist, string album, string filePathOverride = null) {
        if (bytes == null || bytes.Length < 256) return (null, null);
        Bitmap bmp = MediaServiceUtils.DecodeScaledBitmapSafe(bytes, 640);
        if (bmp == null) return (null, null);

        string imgExt = DetectImageExtension(bytes);
        string savedPath = null;
        try {
            string dir = GetCacheSubdir(ctx, "covers");
            string cleanAlb = IsGenericAlbumName(album) ? "" : album.Trim();
            if (!string.IsNullOrEmpty(dir)) {
                string trkHash = !string.IsNullOrEmpty(cleanAlb)
                    ? HashKey($"trk|{artist}|{cleanAlb}|{title}")
                    : HashKey($"trk|{artist}|{title}");
                savedPath = WriteCachedImageAtomic(dir, trkHash, imgExt, bytes);
                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrEmpty(cleanAlb)) {
                    WriteCachedImageAtomic(dir, HashKey($"alb|{artist}|{cleanAlb}"), imgExt, bytes);
                }
            }
            if (!string.IsNullOrWhiteSpace(filePathOverride)) {
                string ovrDir = GetCacheSubdir(ctx, "cover_overrides");
                if (!string.IsNullOrEmpty(ovrDir)) {
                    savedPath = WriteCachedImageAtomic(ovrDir, HashKey($"file|{filePathOverride}"), imgExt, bytes);
                }
                MediaLibraryService.InvalidateThumbCache(filePathOverride);
            }
        } catch {
            // Keep decoded bitmap even if app-private cache write fails
        }
        return (bmp, savedPath);
    }

    [Flags]
    enum LyricsEditionFlags {
        None = 0,
        Live = 1 << 0,
        Remix = 1 << 1,
        Instrumental = 1 << 2,
        Acoustic = 1 << 3,
        SpeedMod = 1 << 4,
        RadioEdit = 1 << 5,
        Remaster = 1 << 6
    }

    static readonly Regex EdLiveRegex = new(@"\b(live|unplugged|session|sessions|concert|tour)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EdRemixRegex = new(@"\b(remix|rmx|vip|dub\s+mix|club\s+mix|mashup|bootleg|rework)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EdInstRegex = new(@"\b(instrumental|karaoke|off\s+vocal|backing\s+track|no\s+vocals|acapella|a\s+capella)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EdAcousticRegex = new(@"\b(acoustic|piano\s+version|orchestral|stripped)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EdSpeedRegex = new(@"\b(slowed|sped\s+up|speed\s+up|nightcore|reverb)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EdRadioRegex = new(@"\b(radio\s+edit|single\s+edit|tv\s+size|short\s+edit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EdRemasterRegex = new(@"\b(remaster|remastered)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LrcLineTimestampCheckRegex = new(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);

    static LyricsEditionFlags ExtractEditionFlags(string text) {
        if (string.IsNullOrWhiteSpace(text)) return LyricsEditionFlags.None;
        LyricsEditionFlags f = LyricsEditionFlags.None;
        if (EdLiveRegex.IsMatch(text)) f |= LyricsEditionFlags.Live;
        if (EdRemixRegex.IsMatch(text)) f |= LyricsEditionFlags.Remix;
        if (EdInstRegex.IsMatch(text)) f |= LyricsEditionFlags.Instrumental;
        if (EdAcousticRegex.IsMatch(text)) f |= LyricsEditionFlags.Acoustic;
        if (EdSpeedRegex.IsMatch(text)) f |= LyricsEditionFlags.SpeedMod;
        if (EdRadioRegex.IsMatch(text)) f |= LyricsEditionFlags.RadioEdit;
        if (EdRemasterRegex.IsMatch(text)) f |= LyricsEditionFlags.Remaster;
        return f;
    }

    static string StripEditionBase(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = CleanSearchTerm(raw);
        s = AllBracketsRegex().Replace(s, " ");
        s = TrailingEditionDashRegex().Replace(s, "");
        return NormalizeAlnumSpaces(MultiSpaceRegex().Replace(s, " ").Trim());
    }

    static double TokenOverlapSimilarity(string normA, string normB) {
        if (string.IsNullOrEmpty(normA) || string.IsNullOrEmpty(normB)) return 0;
        if (normA == normB) return 1.0;
        string[] tokA = normA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] tokB = normB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokA.Length == 0 || tokB.Length == 0) return 0;
        HashSet<string> setA = new(tokA, StringComparer.Ordinal);
        HashSet<string> setB = new(tokB, StringComparer.Ordinal);
        int inter = 0;
        foreach (string t in setA) {
            if (setB.Contains(t)) inter++;
        }
        int union = setA.Count + setB.Count - inter;
        return union > 0 ? (double)inter / union : 0;
    }

    public static (int RiskScore, string Notes, bool IsExactMatch, int MatchScore) ComputeLyricsMatchRisk(
        string queryTitle,
        string queryArtist,
        string queryAlbum,
        double expectedDurationSec,
        OnlineLyricsCandidate candidate) {
        string cleanQTitle = CleanSearchTerm(queryTitle);
        string cleanQArtist = CleanSearchTerm(queryArtist);
        string cleanQAlbum = IsGenericAlbumName(queryAlbum) ? "" : CleanAlbumForSearch(queryAlbum);

        string normFullQTitle = NormalizeAlnumSpaces(cleanQTitle);
        string normFullCTitle = NormalizeAlnumSpaces(CleanSearchTerm(candidate.TrackName));
        string normBaseQTitle = StripEditionBase(queryTitle);
        string normBaseCTitle = StripEditionBase(candidate.TrackName);
        string normQArtist = NormalizeCompare(cleanQArtist);
        string normQAlbum = NormalizeCompareFull(cleanQAlbum);
        string normBaseQAlbum = NormalizeAlnumSpaces(StripAllBrackets(cleanQAlbum));

        int risk = 0;
        List<string> notes = [];

        // 1. Title similarity risk
        bool fullTitleMatch = !string.IsNullOrEmpty(normFullQTitle) && normFullCTitle == normFullQTitle;
        bool baseTitleMatch = !string.IsNullOrEmpty(normBaseQTitle) && normBaseCTitle == normBaseQTitle;
        double titleSim = baseTitleMatch ? 1.0 : TokenOverlapSimilarity(normBaseQTitle, normBaseCTitle);
        if (!baseTitleMatch) {
            bool contained = !string.IsNullOrEmpty(normBaseQTitle) && !string.IsNullOrEmpty(normBaseCTitle) &&
                (normBaseCTitle.Contains(normBaseQTitle, StringComparison.Ordinal) ||
                 normBaseQTitle.Contains(normBaseCTitle, StringComparison.Ordinal));
            if (contained || titleSim >= 0.80) {
                risk += 12;
                notes.Add("near title");
            } else if (titleSim >= 0.55) {
                risk += 28;
                notes.Add("partial title");
            } else {
                risk += 55;
                notes.Add("title mismatch");
            }
        }

        // 2. Edition / version mismatch risk
        LyricsEditionFlags qEd = ExtractEditionFlags(cleanQTitle);
        LyricsEditionFlags cEd = ExtractEditionFlags(candidate.TrackName);
        if (cEd == LyricsEditionFlags.None && !string.IsNullOrWhiteSpace(candidate.AlbumName)) {
            cEd = ExtractEditionFlags(candidate.AlbumName) &
                (LyricsEditionFlags.Live | LyricsEditionFlags.Instrumental | LyricsEditionFlags.Acoustic | LyricsEditionFlags.Remix);
        }
        LyricsEditionFlags edDiff = qEd ^ cEd;
        bool majorEditionMismatch = false;
        if ((edDiff & LyricsEditionFlags.Instrumental) != 0) {
            risk += 70;
            majorEditionMismatch = true;
            notes.Add("edition: instrumental/karaoke");
        }
        if ((edDiff & LyricsEditionFlags.Live) != 0) {
            risk += 55;
            majorEditionMismatch = true;
            notes.Add("edition: live/unplugged");
        }
        if ((edDiff & LyricsEditionFlags.Remix) != 0) {
            risk += 55;
            majorEditionMismatch = true;
            notes.Add("edition: remix");
        }
        if ((edDiff & LyricsEditionFlags.SpeedMod) != 0) {
            risk += 55;
            majorEditionMismatch = true;
            notes.Add("edition: slowed/sped up");
        }
        if ((edDiff & LyricsEditionFlags.Acoustic) != 0) {
            risk += 48;
            majorEditionMismatch = true;
            notes.Add("edition: acoustic");
        }
        if ((edDiff & LyricsEditionFlags.RadioEdit) != 0) {
            risk += 45;
            majorEditionMismatch = true;
            notes.Add("edition: radio edit");
        }
        if (!majorEditionMismatch && (edDiff & LyricsEditionFlags.Remaster) != 0) {
            risk += 4;
        }

        // 3. Artist match risk
        bool artistMatch = false;
        if (!string.IsNullOrEmpty(normQArtist)) {
            artistMatch = ArtistExactMatch(candidate.ArtistName, cleanQArtist);
            if (!artistMatch) {
                string normCArtist = NormalizeCompare(candidate.ArtistName);
                double artSim = TokenOverlapSimilarity(normQArtist, normCArtist);
                bool partialArtist = !string.IsNullOrEmpty(normCArtist) &&
                    (normCArtist.Contains(normQArtist, StringComparison.Ordinal) ||
                     normQArtist.Contains(normCArtist, StringComparison.Ordinal) ||
                     artSim >= 0.45);
                if (partialArtist) {
                    risk += 18;
                    notes.Add("partial artist");
                } else {
                    risk += 45;
                    notes.Add("artist mismatch");
                }
            }
        } else {
            risk += 10;
            notes.Add("no query artist");
        }

        // 4. Album match risk
        bool albumMatch = false;
        if (!string.IsNullOrEmpty(normQAlbum)) {
            string normCAlbum = NormalizeCompareFull(candidate.AlbumName);
            string normBaseCAlbum = NormalizeAlnumSpaces(StripAllBrackets(candidate.AlbumName));
            if (normCAlbum == normQAlbum) {
                albumMatch = true;
            } else if (!string.IsNullOrEmpty(normBaseQAlbum) && normBaseCAlbum == normBaseQAlbum) {
                risk += 3;
            } else {
                risk += 8;
            }
        }

        // 5. Tight duration delta risk (<=3s 0, <=6s +10, <=12s +28, >15s +55)
        bool durTight = false;
        bool durOk = true;
        if (expectedDurationSec > 1 && candidate.DurationSec > 1) {
            double delta = Math.Abs(candidate.DurationSec - expectedDurationSec);
            if (delta <= 3.0) {
                durTight = true;
            } else if (delta <= 6.0) {
                risk += 10;
                notes.Add($"dur Δ{delta:F1}s");
            } else if (delta <= 12.0) {
                risk += 28;
                notes.Add($"dur Δ{delta:F1}s");
            } else if (delta <= 15.0) {
                risk += 40;
                notes.Add($"dur Δ{delta:F1}s");
            } else {
                risk += 55;
                durOk = false;
                notes.Add($"dur Δ{delta:F0}s >15s");
            }
        } else if (expectedDurationSec > 1) {
            risk += 8;
        }

        int riskScore = Math.Clamp(risk, 0, 100);
        bool exact = riskScore <= 25 && (fullTitleMatch || baseTitleMatch) && artistMatch && durOk && !majorEditionMismatch;
        int syncBonus = candidate.IsSyllableSynced ? 450 : (candidate.IsSynced ? 250 : 0);
        int matchScore = (fullTitleMatch ? 550 : (baseTitleMatch ? 480 : (int)Math.Round(titleSim * 280)))
            + (artistMatch ? 400 : 0)
            + (albumMatch ? 120 : 0)
            + syncBonus
            + (durTight ? 160 : (durOk ? 30 : -220))
            - (riskScore * 8);

        return (riskScore, string.Join("; ", notes), exact, matchScore);
    }

    public static async Task<List<OnlineLyricsCandidate>> SearchLyricsAsync(
        string title,
        string artist,
        string album = "",
        CancellationToken ct = default,
        double expectedDurationSec = 0) {
        List<OnlineLyricsCandidate> list = [];
        string cleanTitle = CleanSearchTerm(title);
        string cleanArtist = CleanSearchTerm(artist);
        string fullAlbum = IsGenericAlbumName(album) ? "" : CleanAlbumForSearch(album);
        if (string.IsNullOrWhiteSpace(cleanTitle)) return list;

        // Source 1: LRCLIB (exact endpoint with album_name + broad search)
        try {
            if (!string.IsNullOrWhiteSpace(cleanArtist)) {
                string exactUrl = !string.IsNullOrWhiteSpace(fullAlbum)
                    ? $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}&album_name={Uri.EscapeDataString(fullAlbum)}"
                    : $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
                using HttpResponseMessage resp = await http.GetAsync(exactUrl, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    OnlineLyricsCandidate cand = ParseLrcLibItem(doc.RootElement);
                    if (cand != null) list.Add(cand);
                } else if (!string.IsNullOrWhiteSpace(fullAlbum)) {
                    string fbUrl = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
                    using HttpResponseMessage fbResp = await http.GetAsync(fbUrl, ct).ConfigureAwait(false);
                    if (fbResp.IsSuccessStatusCode) {
                        string json = await fbResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        OnlineLyricsCandidate cand = ParseLrcLibItem(doc.RootElement);
                        if (cand != null) list.Add(cand);
                    }
                }
            }
        } catch {
            // Fall through to query search
        }

        try {
            string q = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} {cleanTitle}";
            string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(q)}";
            using HttpResponseMessage resp = await http.GetAsync(searchUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) {
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement el in doc.RootElement.EnumerateArray()) {
                        OnlineLyricsCandidate cand = ParseLrcLibItem(el);
                        if (cand != null && !list.Any(x => x.SyncedLrc == cand.SyncedLrc && x.PlainLyrics == cand.PlainLyrics)) {
                            list.Add(cand);
                        }
                        if (list.Count >= 6) break;
                    }
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.LRCLIB: {ex.Message}");
        }

        // Source 2 & 3: Netease Cloud Music (yrc/klyric/lrc) & KuGou (krc/lrc) — never skip just because LRCLIB returned line-synced!
        if (!list.Any(l => l.IsSyllableSynced)) {
            List<OnlineLyricsCandidate> neteaseResults = [];
            List<OnlineLyricsCandidate> kugouResults = [];
            await Task.WhenAll(
                TrySearchNeteaseLyricsAsync(cleanTitle, cleanArtist, neteaseResults, ct),
                TrySearchKugouLyricsAsync(cleanTitle, cleanArtist, expectedDurationSec, kugouResults, ct)
            ).ConfigureAwait(false);

            foreach (OnlineLyricsCandidate cand in neteaseResults.Concat(kugouResults)) {
                if (!list.Any(x => x.BestContent == cand.BestContent)) {
                    list.Add(cand);
                }
            }
        }

        // Source 4: Lyrics.ovh (plain lyrics fallback)
        if (!string.IsNullOrWhiteSpace(cleanArtist) && !list.Any(l => l.IsSynced)) {
            await TrySearchLyricsOvhAsync(cleanTitle, cleanArtist, list, ct).ConfigureAwait(false);
        }

        List<OnlineLyricsCandidate> ranked = list
            .Select(c => {
                var (riskScore, riskNotes, exact, score) = ComputeLyricsMatchRisk(title, artist, fullAlbum, expectedDurationSec, c);
                int rank = (c.Source, c.IsSyllableSynced, c.IsSynced) switch {
                    ("Netease", true, _) => 1,
                    ("KuGou", true, _) => 2,
                    ("LRCLIB", true, _) => 1,
                    ("LRCLIB", false, true) => 3,
                    ("Netease", false, true) => 4,
                    ("KuGou", false, true) => 5,
                    ("LRCLIB", _, false) => 6,
                    ("Netease", _, false) => 7,
                    ("KuGou", _, false) => 8,
                    ("Lyrics.ovh", _, _) => 9,
                    _ => 10
                };
                string sourceDisplay = c.IsSyllableSynced
                    ? $"{c.Source} Syllable"
                    : (c.IsSynced ? $"{c.Source} Synced" : c.Source);

                return new OnlineLyricsCandidate {
                    TrackName = c.TrackName,
                    ArtistName = c.ArtistName,
                    AlbumName = c.AlbumName,
                    Source = sourceDisplay,
                    IsSynced = c.IsSynced,
                    IsSyllableSynced = c.IsSyllableSynced,
                    SyncedLrc = c.SyncedLrc,
                    PlainLyrics = c.PlainLyrics,
                    DurationSec = c.DurationSec,
                    SourceRank = rank,
                    MatchScore = score,
                    MatchRiskScore = riskScore,
                    MatchRiskNotes = riskNotes,
                    IsExactMatch = exact
                };
            })
            .OrderByDescending(c => c.MatchRiskScore <= 45)
            .ThenByDescending(c => c.IsSyllableSynced ? 3 : (c.IsSynced ? 2 : 1))
            .ThenBy(c => c.MatchRiskScore)
            .ThenByDescending(c => c.MatchScore)
            .ToList();

        // W4 Stage-B: Find the cleanest verified plain-lyrics reference (preferring LRCLIB) to scrub
        // leading/trailing credit legends from synced & syllable-synced providers (NetEase/KuGou/etc.).
        string cleanPlainRef = ranked
            .Where(c => c.MatchRiskScore <= 35 && !string.IsNullOrWhiteSpace(c.PlainLyrics))
            .OrderByDescending(c => c.Source.StartsWith("LRCLIB", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.MatchRiskScore)
            .Select(c => c.PlainLyrics)
            .FirstOrDefault();

        for (int i = 0; i < ranked.Count; i++) {
            OnlineLyricsCandidate cand = ranked[i];
            if (!cand.IsSynced || string.IsNullOrWhiteSpace(cand.SyncedLrc)) {
                continue;
            }
            string refPlain = !string.IsNullOrWhiteSpace(cand.PlainLyrics) ? cand.PlainLyrics : cleanPlainRef;
            LyricsDocument parsed = LyricsService.ParseLyricsText(cand.SyncedLrc, cand.Source);
            if (!parsed.IsSynced || parsed.Lines.Count == 0) {
                continue;
            }
            LyricsDocument cleaned = LyricsService.AlignAndCleanWithPlainLyrics(parsed, refPlain, title, artist);
            if (cleaned.IsSynced && cleaned.Lines.Count > 0) {
                string serialized = LyricsService.SerializeToLrc(cleaned);
                if (!string.IsNullOrWhiteSpace(serialized)) {
                    ranked[i] = new OnlineLyricsCandidate {
                        TrackName = cand.TrackName,
                        ArtistName = cand.ArtistName,
                        AlbumName = cand.AlbumName,
                        Source = cand.Source,
                        IsSynced = true,
                        IsSyllableSynced = cleaned.IsSyllableSynced,
                        SyncedLrc = serialized,
                        PlainLyrics = cand.PlainLyrics,
                        DurationSec = cand.DurationSec,
                        SourceRank = cand.SourceRank,
                        MatchScore = cand.MatchScore,
                        MatchRiskScore = cand.MatchRiskScore,
                        MatchRiskNotes = cand.MatchRiskNotes,
                        IsExactMatch = cand.IsExactMatch
                    };
                }
            }
        }

        return ranked;
    }

    static OnlineLyricsCandidate ParseLrcLibItem(JsonElement el) {
        string synced = el.TryGetProperty("syncedLyrics", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String
            ? (sEl.GetString() ?? "")
            : "";
        string plain = el.TryGetProperty("plainLyrics", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String
            ? (pEl.GetString() ?? "")
            : "";
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain)) return null;

        bool isSynced = !string.IsNullOrWhiteSpace(synced);
        bool isSyllable = isSynced && LyricsService.HasSyllableTags(synced);
        return new OnlineLyricsCandidate {
            TrackName = el.TryGetProperty("trackName", out JsonElement tEl) ? (tEl.GetString() ?? "") : "",
            ArtistName = el.TryGetProperty("artistName", out JsonElement arEl) ? (arEl.GetString() ?? "") : "",
            AlbumName = el.TryGetProperty("albumName", out JsonElement alEl) ? (alEl.GetString() ?? "") : "",
            Source = "LRCLIB",
            IsSynced = isSynced,
            IsSyllableSynced = isSyllable,
            SyncedLrc = synced,
            PlainLyrics = plain,
            DurationSec = el.TryGetProperty("duration", out JsonElement dEl) && dEl.TryGetDouble(out double d) ? d : 0
        };
    }

    static string ExtractJsonLyricField(JsonElement root, string fieldName) {
        if (root.TryGetProperty(fieldName, out JsonElement obj) &&
            obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty("lyric", out JsonElement lyrEl) &&
            lyrEl.ValueKind == JsonValueKind.String) {
            return lyrEl.GetString() ?? "";
        }
        return "";
    }

    static async Task TrySearchNeteaseLyricsAsync(
        string cleanTitle,
        string cleanArtist,
        List<OnlineLyricsCandidate> results,
        CancellationToken ct) {
        try {
            string q = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} {cleanTitle}";
            string searchUrl = $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(q)}&type=1&limit=4&offset=0";
            using HttpRequestMessage req = new(HttpMethod.Get, searchUrl);
            req.Headers.Referrer = new Uri("https://music.163.com/");
            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out JsonElement resEl) ||
                !resEl.TryGetProperty("songs", out JsonElement songsEl) ||
                songsEl.ValueKind != JsonValueKind.Array) {
                return;
            }

            foreach (JsonElement song in songsEl.EnumerateArray()) {
                long songId = song.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long sid) ? sid : 0;
                if (songId <= 0) continue;

                string songName = song.TryGetProperty("name", out JsonElement nEl) ? (nEl.GetString() ?? "") : "";
                double durSec = song.TryGetProperty("duration", out JsonElement dEl) && dEl.TryGetDouble(out double dMs) ? dMs / 1000.0 : 0;

                string artistName = "";
                if (song.TryGetProperty("artists", out JsonElement arArr) && arArr.ValueKind == JsonValueKind.Array) {
                    List<string> names = [];
                    foreach (JsonElement aEl in arArr.EnumerateArray()) {
                        string an = aEl.TryGetProperty("name", out JsonElement anEl) ? (anEl.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(an)) names.Add(an);
                    }
                    artistName = string.Join(", ", names);
                }
                string albName = song.TryGetProperty("album", out JsonElement alEl) && alEl.TryGetProperty("name", out JsonElement alNameEl)
                    ? (alNameEl.GetString() ?? "")
                    : "";

                string lyrUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1&rv=-1&yv=1";
                using HttpRequestMessage lyrReq = new(HttpMethod.Get, lyrUrl);
                lyrReq.Headers.Referrer = new Uri("https://music.163.com/");
                using HttpResponseMessage lyrResp = await http.SendAsync(lyrReq, ct).ConfigureAwait(false);
                if (!lyrResp.IsSuccessStatusCode) continue;

                string lyrJson = await lyrResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument lyrDoc = JsonDocument.Parse(lyrJson);
                string yrc = ExtractJsonLyricField(lyrDoc.RootElement, "yrc");
                string klyric = ExtractJsonLyricField(lyrDoc.RootElement, "klyric");
                string lrc = ExtractJsonLyricField(lyrDoc.RootElement, "lrc");

                string chosen = "";
                bool isSyllable = false;
                bool isSynced = false;

                if (LyricsService.HasSyllableTags(yrc)) {
                    chosen = yrc;
                    isSyllable = true;
                    isSynced = true;
                } else if (LyricsService.HasSyllableTags(klyric)) {
                    chosen = klyric;
                    isSyllable = true;
                    isSynced = true;
                } else if (!string.IsNullOrWhiteSpace(lrc) && LrcLineTimestampCheckRegex.IsMatch(lrc)) {
                    chosen = lrc;
                    isSyllable = LyricsService.HasSyllableTags(lrc);
                    isSynced = true;
                } else if (!string.IsNullOrWhiteSpace(klyric) && LrcLineTimestampCheckRegex.IsMatch(klyric)) {
                    chosen = klyric;
                    isSynced = true;
                } else {
                    chosen = !string.IsNullOrWhiteSpace(lrc) ? lrc : (!string.IsNullOrWhiteSpace(klyric) ? klyric : yrc);
                }

                if (string.IsNullOrWhiteSpace(chosen)) continue;

                results.Add(new OnlineLyricsCandidate {
                    TrackName = songName,
                    ArtistName = artistName,
                    AlbumName = albName,
                    Source = "Netease",
                    IsSynced = isSynced,
                    IsSyllableSynced = isSyllable,
                    SyncedLrc = isSynced ? chosen : "",
                    PlainLyrics = isSynced ? "" : chosen,
                    DurationSec = durSec
                });
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.Netease: {ex.Message}");
        }
    }

    static async Task TrySearchKugouLyricsAsync(
        string cleanTitle,
        string cleanArtist,
        double expectedDurationSec,
        List<OnlineLyricsCandidate> results,
        CancellationToken ct) {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5.5));
        CancellationToken kCt = timeoutCts.Token;
        try {
            string q = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} - {cleanTitle}";
            long durMs = expectedDurationSec > 1 ? (long)Math.Round(expectedDurationSec * 1000.0) : 0;
            string durParam = durMs > 0 ? durMs.ToString(CultureInfo.InvariantCulture) : "";
            string searchUrl = $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(q)}&duration={durParam}&hash=";
            using HttpResponseMessage resp = await http.GetAsync(searchUrl, kCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync(kCt).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candsEl) ||
                candsEl.ValueKind != JsonValueKind.Array) {
                return;
            }

            int tried = 0;
            foreach (JsonElement candEl in candsEl.EnumerateArray()) {
                if (tried >= 3) break;
                string id = candEl.TryGetProperty("id", out JsonElement idEl) ? idEl.ToString() : "";
                string accesskey = candEl.TryGetProperty("accesskey", out JsonElement akEl) ? (akEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accesskey)) continue;
                tried++;

                string songName = candEl.TryGetProperty("song", out JsonElement sEl) ? (sEl.GetString() ?? "") : cleanTitle;
                string singer = candEl.TryGetProperty("singer", out JsonElement siEl) ? (siEl.GetString() ?? "") : cleanArtist;
                double candDurSec = candEl.TryGetProperty("duration", out JsonElement dEl) && dEl.TryGetDouble(out double dMs)
                    ? dMs / 1000.0
                    : 0;

                string decodedText = await DownloadKugouLyricContentAsync(id, accesskey, "krc", kCt).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(decodedText)) {
                    decodedText = await DownloadKugouLyricContentAsync(id, accesskey, "lrc", kCt).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(decodedText)) continue;

                LyricsDocument parsed = LyricsService.ParseLyricsText(decodedText, "KuGou");
                if (!parsed.HasLyrics) continue;

                results.Add(new OnlineLyricsCandidate {
                    TrackName = string.IsNullOrWhiteSpace(songName) ? cleanTitle : songName,
                    ArtistName = string.IsNullOrWhiteSpace(singer) ? cleanArtist : singer,
                    Source = "KuGou",
                    IsSynced = parsed.IsSynced,
                    IsSyllableSynced = parsed.IsSyllableSynced,
                    SyncedLrc = parsed.IsSynced ? decodedText : "",
                    PlainLyrics = parsed.IsSynced ? "" : decodedText,
                    DurationSec = candDurSec
                });
            }
        } catch (OperationCanceledException) {
            // Timeout or caller cancellation — fail gracefully
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.KuGou: {ex.Message}");
        }
    }

    static async Task<string> DownloadKugouLyricContentAsync(string id, string accesskey, string fmt, CancellationToken ct) {
        try {
            string dlUrl = $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accesskey)}&fmt={fmt}&charset=utf8";
            using HttpResponseMessage resp = await http.GetAsync(dlUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return "";

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("content", out JsonElement cEl) || cEl.ValueKind != JsonValueKind.String) {
                return "";
            }
            string b64 = cEl.GetString();
            if (string.IsNullOrWhiteSpace(b64)) return "";
            byte[] bytes = Convert.FromBase64String(b64);
            return LyricsService.TryDecodeKrcBytes(bytes);
        } catch {
            return "";
        }
    }

    static async Task TrySearchLyricsOvhAsync(
        string cleanTitle,
        string cleanArtist,
        List<OnlineLyricsCandidate> results,
        CancellationToken ct) {
        try {
            string url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(cleanArtist)}/{Uri.EscapeDataString(cleanTitle)}";
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            string plain = doc.RootElement.TryGetProperty("lyrics", out JsonElement lEl) ? (lEl.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(plain)) return;

            results.Add(new OnlineLyricsCandidate {
                TrackName = cleanTitle,
                ArtistName = cleanArtist,
                Source = "Lyrics.ovh",
                IsSynced = false,
                PlainLyrics = plain
            });
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.LyricsOvh: {ex.Message}");
        }
    }

    public static async Task<List<OnlineSubtitleCandidate>> SearchSubtitlesAsync(
        string query,
        string lang = "pb",
        CancellationToken ct = default) {
        List<OnlineSubtitleCandidate> list = [];
        string clean = CleanSearchTerm(query);
        if (string.IsNullOrWhiteSpace(clean)) return list;

        string[] osLangs = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
            ? ["eng"]
            : ["pob", "por", "eng"];

        // Source 1: OpenSubtitles REST API (reliable GZip direct download)
        foreach (string osLang in osLangs) {
            try {
                string url = $"https://rest.opensubtitles.org/search/query-{Uri.EscapeDataString(clean)}/sublanguageid-{osLang}";
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd(AppUserAgent);
                using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement item in doc.RootElement.EnumerateArray()) {
                    string dl = item.TryGetProperty("SubDownloadLink", out JsonElement dEl) ? (dEl.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(dl)) continue;
                    string id = item.TryGetProperty("IDSubtitleFile", out JsonElement iEl) ? (iEl.GetString() ?? "") : "";
                    string subFile = item.TryGetProperty("SubFileName", out JsonElement fEl) ? (fEl.GetString() ?? "") : "";
                    string mvName = item.TryGetProperty("MovieName", out JsonElement mEl) ? (mEl.GetString() ?? "") : "";
                    string langName = item.TryGetProperty("LanguageName", out JsonElement lEl) ? (lEl.GetString() ?? osLang) : osLang;
                    string dispTitle = !string.IsNullOrWhiteSpace(subFile) ? subFile : mvName;
                    if (string.IsNullOrWhiteSpace(dispTitle)) dispTitle = clean;

                    list.Add(new OnlineSubtitleCandidate {
                        Id = string.IsNullOrEmpty(id) ? dl : id,
                        Title = dispTitle.Trim(),
                        Language = langName,
                        DownloadUrl = dl,
                        Source = "OpenSubtitles",
                        SourceRank = 1
                    });
                    if (list.Count >= 10) break;
                }
                if (list.Count >= 6) break;
            } catch (Exception ex) {
                AppLogger.Warn($"OnlineMetadataService.OpenSubtitles({osLang}): {ex.Message}");
            }
        }

        // Source 2: Podnapisi legacy XML fallback (only if OpenSubtitles had < 3 results)
        if (list.Count < 3) {
            string[] podLangs = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                ? ["en"]
                : ["pb", "pt", "en"];

            foreach (string l in podLangs) {
                try {
                    string url = $"https://www.podnapisi.net/subtitles/search/old?sXML=1&sL={l}&sK={Uri.EscapeDataString(clean)}";
                    using HttpRequestMessage req = new(HttpMethod.Get, url);
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                    using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) continue;

                    string xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<subtitle", StringComparison.OrdinalIgnoreCase)) continue;

                    XDocument xdoc = XDocument.Parse(xml);
                    foreach (XElement sub in xdoc.Descendants("subtitle")) {
                        string id = sub.Element("id")?.Value ?? "";
                        string title = sub.Element("release")?.Value;
                        if (string.IsNullOrWhiteSpace(title)) title = sub.Element("title")?.Value ?? clean;
                        string langName = sub.Element("languageName")?.Value ?? l.ToUpperInvariant();
                        string subUrl = sub.Element("url")?.Value ?? "";
                        string dlUrl = !string.IsNullOrEmpty(subUrl)
                            ? (subUrl.EndsWith("/download", StringComparison.OrdinalIgnoreCase) ? subUrl : subUrl.TrimEnd('/') + "/download")
                            : (!string.IsNullOrEmpty(id) ? $"https://www.podnapisi.net/subtitles/{id}/download" : "");
                        if (string.IsNullOrEmpty(dlUrl)) continue;

                        list.Add(new OnlineSubtitleCandidate {
                            Id = id,
                            Title = title.Trim(),
                            Language = langName,
                            DownloadUrl = dlUrl,
                            Source = "Podnapisi",
                            SourceRank = 2
                        });
                        if (list.Count >= 14) break;
                    }
                    if (list.Count >= 6) break;
                } catch (Exception ex) {
                    AppLogger.Warn($"OnlineMetadataService.Subtitles({l}): {ex.Message}");
                }
            }
        }

        string normQuery = NormalizeCompare(clean);
        string[] tokens = normQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string targetLangHint = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "english" : "portug";

        return list
            .Select(s => {
                string normTitle = NormalizeCompare(s.Title);
                int tokenHits = tokens.Count(tok => tok.Length >= 2 && normTitle.Contains(tok));
                bool allTokensHit = tokens.Length > 0 && tokenHits == tokens.Length;
                bool langHit = (s.Language ?? "").Contains(targetLangHint, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(lang, "pb", StringComparison.OrdinalIgnoreCase) && (s.Language ?? "").Contains("brazil", StringComparison.OrdinalIgnoreCase));
                int score = tokenHits * 150 + (allTokensHit ? 400 : 0) + (langHit ? 250 : 0) - (s.SourceRank - 1) * 50;
                return new OnlineSubtitleCandidate {
                    Id = s.Id,
                    Title = s.Title,
                    Language = s.Language,
                    DownloadUrl = s.DownloadUrl,
                    Source = s.Source,
                    SourceRank = s.SourceRank,
                    MatchScore = score,
                    IsExactMatch = allTokensHit && langHit
                };
            })
            .OrderByDescending(s => s.IsExactMatch)
            .ThenBy(s => s.SourceRank)
            .ThenByDescending(s => s.MatchScore)
            .ToList();
    }

    static string DetectSubtitleExtension(byte[] bytes, string hintName = null) {
        if (!string.IsNullOrEmpty(hintName)) {
            string ext = IOPath.GetExtension(hintName).ToLowerInvariant();
            if (ext is ".ass" or ".ssa" or ".vtt" or ".srt") return ext;
        }
        int probeLen = Math.Min(bytes.Length, 1024);
        string head = MediaServiceUtils.ReadTextAutoEncoding(bytes.AsSpan(0, probeLen).ToArray()).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) return ".vtt";
        if (head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) {
            return ".ass";
        }
        return ".srt";
    }

    public static async Task<string> DownloadAndExtractSubtitleAsync(
        Context ctx,
        OnlineSubtitleCandidate candidate,
        CancellationToken ct = default) {
        if (candidate == null || string.IsNullOrEmpty(candidate.DownloadUrl)) return null;
        try {
            using HttpRequestMessage req = new(HttpMethod.Get, candidate.DownloadUrl);
            req.Headers.Accept.Clear();
            if (candidate.DownloadUrl.Contains("opensubtitles.org", StringComparison.OrdinalIgnoreCase)) {
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd(AppUserAgent);
            }
            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length < 16) return null;

            string dir = GetCacheSubdir(ctx, "subtitles");
            if (string.IsNullOrEmpty(dir)) return null;
            string safeBase = HashKey($"{candidate.Id}|{candidate.Title}");

            // GZip archive starts with 0x1F, 0x8B (OpenSubtitles SubDownloadLink)
            if (bytes[0] == 0x1F && bytes[1] == 0x8B) {
                using MemoryStream ms = new(bytes);
                using GZipStream gz = new(ms, CompressionMode.Decompress);
                using MemoryStream decomp = new();
                await gz.CopyToAsync(decomp, ct).ConfigureAwait(false);
                byte[] subBytes = decomp.ToArray();
                string outPath = IOPath.Combine(dir, safeBase + DetectSubtitleExtension(subBytes, candidate.Title));
                MediaServiceUtils.WriteBytesAtomic(outPath, subBytes);
                return outPath;
            }

            // ZIP archive starts with 'PK' (0x50, 0x4B)
            if (bytes[0] == 0x50 && bytes[1] == 0x4B) {
                using MemoryStream ms = new(bytes);
                using ZipArchive zip = new(ms, ZipArchiveMode.Read);
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                using Stream es = entry.Open();
                using MemoryStream decomp = new();
                await es.CopyToAsync(decomp, ct).ConfigureAwait(false);
                byte[] subBytes = decomp.ToArray();
                string outPath = IOPath.Combine(dir, safeBase + DetectSubtitleExtension(subBytes, entry.Name));
                MediaServiceUtils.WriteBytesAtomic(outPath, subBytes);
                return outPath;
            }

            string rawPath = IOPath.Combine(dir, safeBase + DetectSubtitleExtension(bytes, candidate.Title));
            MediaServiceUtils.WriteBytesAtomic(rawPath, bytes);
            return rawPath;
        } catch (Exception ex) {
            AppLogger.Error("OnlineMetadataService.DownloadSubtitle", ex);
            return null;
        }
    }

    public static async Task<OnlineEnrichResult> AutoEnrichIfMissingAsync(
        Context ctx,
        MediaArtworkMetadata meta,
        CancellationToken ct = default) {
        if (ctx == null || meta == null) return null;

        bool needCover = !meta.HasEmbeddedArtwork;
        int currentLyricsTier = LyricsService.GetSyncTier(meta.Lyrics);
        bool needLyrics = !meta.HasVideo && currentLyricsTier < 2;
        if (!needCover && !needLyrics) return null;

        Bitmap fetchedArt = null;
        string artSource = "";
        LyricsDocument fetchedLyrics = null;
        string lyrSource = "";

        Task coverTask = Task.Run(async () => {
            if (!needCover) return;
            List<OnlineCoverCandidate> covers = await SearchCoverCandidatesAsync(
                meta.Title, meta.Artist, meta.Album, meta.HasVideo, ct).ConfigureAwait(false);
            // Auto-enrich silently ONLY from verified exact matches
            IEnumerable<OnlineCoverCandidate> picks = covers.Where(c => c.IsExactMatch).Take(2);
            foreach (OnlineCoverCandidate c in picks) {
                var (bmp, _) = await DownloadAndCacheCoverAsync(ctx, c.ImageUrl, meta.Title, meta.Artist, meta.Album, ct).ConfigureAwait(false);
                if (bmp != null) {
                    fetchedArt = bmp;
                    artSource = c.Source;
                    break;
                }
            }
        }, ct);

        Task lyricsTask = Task.Run(async () => {
            if (!needLyrics) return;
            List<OnlineLyricsCandidate> cands = await SearchLyricsAsync(meta.Title, meta.Artist, meta.Album, ct, meta.DurationSeconds).ConfigureAwait(false);
            OnlineLyricsCandidate best = cands.FirstOrDefault(c =>
                (c.IsExactMatch || (c.IsSynced && c.MatchRiskScore <= 25)) &&
                c.MatchRiskScore <= 45);
            if (best != null && !string.IsNullOrWhiteSpace(best.BestContent)) {
                LyricsDocument doc = LyricsService.ParseLyricsText(best.BestContent, best.Source);
                if (doc.HasLyrics && LyricsService.GetSyncTier(doc) > currentLyricsTier) {
                    SaveLyricsToCache(ctx, meta.Title, meta.Artist, best.BestContent, onlyIfUpgrade: true);
                    fetchedLyrics = doc;
                    lyrSource = doc.SourceLabel;
                }
            }
        }, ct);

        await Task.WhenAll(coverTask, lyricsTask).ConfigureAwait(false);
        if (fetchedArt == null && fetchedLyrics == null) return null;

        return new OnlineEnrichResult {
            ArtworkBitmap = fetchedArt,
            Lyrics = fetchedLyrics,
            ArtworkSource = artSource,
            LyricsSource = lyrSource
        };
    }
}
