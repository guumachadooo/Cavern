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
    public string SyncedLrc { get; init; } = "";
    public string PlainLyrics { get; init; } = "";
    public double DurationSec { get; init; }
    public int SourceRank { get; init; } = 99;
    public int MatchScore { get; init; }
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

    static readonly HttpClient http = CreateHttpClient();

    [GeneratedRegex(@"[\[\(].*?[\]\)]", RegexOptions.Compiled)]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"^\d{1,3}\s*[-._]\s*", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackNumRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(14) };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.8"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://github.com/guumachadooo/Cavern)"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    static string GetCacheSubdir(Context ctx, string sub) {
        string root = ctx?.FilesDir?.AbsolutePath ?? System.IO.Path.GetTempPath();
        string dir = IOPath.Combine(root, "online_cache", sub);
        Directory.CreateDirectory(dir);
        return dir;
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

    static bool IsGenericAlbumName(string album) {
        if (string.IsNullOrWhiteSpace(album)) return true;
        return album.Equals("Download", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Music", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Audiobooks", StringComparison.OrdinalIgnoreCase)
            || album.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeCompare(string raw) {
        string clean = CleanSearchTerm(raw);
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

    public static string GetManualCoverOverridePath(Context ctx, string filePath) {
        if (ctx == null || string.IsNullOrWhiteSpace(filePath)) return null;
        string dir = GetCacheSubdir(ctx, "cover_overrides");
        string p = IOPath.Combine(dir, HashKey($"file|{filePath}") + ".jpg");
        return IOFile.Exists(p) ? p : null;
    }

    public static string GetCachedCoverPath(Context ctx, string title, string artist, string album, string filePath = null) {
        if (ctx == null) return null;
        string ovr = GetManualCoverOverridePath(ctx, filePath);
        if (!string.IsNullOrEmpty(ovr)) return ovr;

        string dir = GetCacheSubdir(ctx, "covers");
        if (!string.IsNullOrWhiteSpace(title)) {
            string pTrk = IOPath.Combine(dir, HashKey($"trk|{artist}|{title}") + ".jpg");
            if (IOFile.Exists(pTrk)) return pTrk;
        }
        if (!string.IsNullOrWhiteSpace(artist) && !IsGenericAlbumName(album)) {
            string pAlb = IOPath.Combine(dir, HashKey($"alb|{artist}|{album}") + ".jpg");
            if (IOFile.Exists(pAlb)) return pAlb;
        }
        return null;
    }

    public static string GetCachedLyricsPath(Context ctx, string title, string artist) {
        if (ctx == null || string.IsNullOrWhiteSpace(title)) return null;
        string dir = GetCacheSubdir(ctx, "lyrics");
        string p = IOPath.Combine(dir, HashKey($"lyr|{artist}|{title}") + ".lrc");
        return IOFile.Exists(p) ? p : null;
    }

    public static string SaveLyricsToCache(Context ctx, string title, string artist, string content) {
        if (ctx == null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) return null;
        string dir = GetCacheSubdir(ctx, "lyrics");
        string p = IOPath.Combine(dir, HashKey($"lyr|{artist}|{title}") + ".lrc");
        IOFile.WriteAllText(p, content, Encoding.UTF8);
        return p;
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
        string cleanAlbum = IsGenericAlbumName(album) ? "" : CleanSearchTerm(album);
        if (string.IsNullOrWhiteSpace(cleanQuery) && string.IsNullOrWhiteSpace(cleanAlbum)) {
            return raw;
        }

        if (isVideo) {
            await TrySearchTmdbAsync(cleanQuery, raw, ct).ConfigureAwait(false);
        }

        string searchTerm = string.IsNullOrWhiteSpace(cleanArtist)
            ? cleanQuery
            : $"{cleanArtist} {(string.IsNullOrWhiteSpace(cleanQuery) ? cleanAlbum : cleanQuery)}";

        await TrySearchItunesAsync(searchTerm, raw, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(cleanAlbum)) {
            await TrySearchAudioDbAsync(cleanArtist, cleanAlbum, raw, ct).ConfigureAwait(false);
            await TrySearchMusicBrainzAsync(cleanArtist, cleanAlbum, raw, ct).ConfigureAwait(false);
        }

        string normTitle = NormalizeCompare(cleanQuery);
        string normArtist = NormalizeCompare(cleanArtist);
        string normAlbum = NormalizeCompare(cleanAlbum);

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
                string cAlbum = NormalizeCompare(c.Album);
                bool titleMatch = !string.IsNullOrEmpty(normTitle) && cTitle == normTitle;
                bool albumMatch = !string.IsNullOrEmpty(normAlbum) && (cAlbum == normAlbum || cTitle == normAlbum);
                bool artistMatch = !string.IsNullOrEmpty(normArtist) && ArtistExactMatch(c.Artist, cleanArtist);

                int score = (titleMatch ? 500 : (!string.IsNullOrEmpty(normTitle) && cTitle.Contains(normTitle) ? 200 : 0))
                    + (artistMatch ? 400 : 0)
                    + (albumMatch ? 120 : 0)
                    - (rank - 1) * 35;

                bool exact = isVideo
                    ? titleMatch
                    : artistMatch && (titleMatch || albumMatch);

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
            .ThenBy(c => c.SourceRank)
            .ThenByDescending(c => c.MatchScore)
            .Take(10)
            .ToList();

        return ranked;
    }

    static async Task TrySearchItunesAsync(string term, List<OnlineCoverCandidate> results, CancellationToken ct) {
        try {
            string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=song&limit=6";
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement item in arr.EnumerateArray()) {
                string art100 = item.TryGetProperty("artworkUrl100", out JsonElement aEl) ? (aEl.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(art100)) continue;
                string hiRes = art100.Replace("100x100bb", "1000x1000bb");
                results.Add(new OnlineCoverCandidate {
                    Title = item.TryGetProperty("trackName", out JsonElement tEl) ? (tEl.GetString() ?? "") : "",
                    Artist = item.TryGetProperty("artistName", out JsonElement arEl) ? (arEl.GetString() ?? "") : "",
                    Album = item.TryGetProperty("collectionName", out JsonElement alEl) ? (alEl.GetString() ?? "") : "",
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
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
            string url = $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&query={Uri.EscapeDataString(titleQuery)}&language=pt-BR";
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
                    Album = "Filme / Vídeo",
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

    static (Bitmap bitmap, string savedPath) SaveAndDecodeCoverBytes(
        Context ctx, byte[] bytes, string title, string artist, string album, string filePathOverride = null) {
        if (bytes == null || bytes.Length < 256) return (null, null);
        Bitmap bmp = MediaMetadataHelper.DecodeScaledBitmap(bytes, 640);
        if (bmp == null) return (null, null);

        string dir = GetCacheSubdir(ctx, "covers");
        string savedPath = IOPath.Combine(dir, HashKey($"trk|{artist}|{title}") + ".jpg");
        IOFile.WriteAllBytes(savedPath, bytes);
        if (!string.IsNullOrWhiteSpace(artist) && !IsGenericAlbumName(album)) {
            string albPath = IOPath.Combine(dir, HashKey($"alb|{artist}|{album}") + ".jpg");
            IOFile.WriteAllBytes(albPath, bytes);
        }
        if (!string.IsNullOrWhiteSpace(filePathOverride)) {
            string ovrDir = GetCacheSubdir(ctx, "cover_overrides");
            string ovrPath = IOPath.Combine(ovrDir, HashKey($"file|{filePathOverride}") + ".jpg");
            IOFile.WriteAllBytes(ovrPath, bytes);
            savedPath = ovrPath;
            MediaLibraryService.InvalidateThumbCache(filePathOverride);
        }
        return (bmp, savedPath);
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
        if (string.IsNullOrWhiteSpace(cleanTitle)) return list;

        // Source 1 & 3: LRCLIB (exact endpoint + broad search)
        try {
            if (!string.IsNullOrWhiteSpace(cleanArtist)) {
                string exactUrl = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
                using HttpResponseMessage resp = await http.GetAsync(exactUrl, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    OnlineLyricsCandidate cand = ParseLrcLibItem(doc.RootElement);
                    if (cand != null) list.Add(cand);
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

        // Source 2: Netease Cloud Music (synced LRC [mm:ss.xxx] fallback/cross-source)
        await TrySearchNeteaseLyricsAsync(cleanTitle, cleanArtist, list, ct).ConfigureAwait(false);

        // Source 4: Lyrics.ovh (plain lyrics fallback)
        if (!string.IsNullOrWhiteSpace(cleanArtist) && !list.Any(l => l.IsSynced)) {
            await TrySearchLyricsOvhAsync(cleanTitle, cleanArtist, list, ct).ConfigureAwait(false);
        }

        string normTitle = NormalizeCompare(cleanTitle);
        string normArtist = NormalizeCompare(cleanArtist);

        return list
            .Select(c => {
                bool titleMatch = !string.IsNullOrEmpty(normTitle) && NormalizeCompare(c.TrackName) == normTitle;
                bool artistMatch = !string.IsNullOrEmpty(normArtist) && ArtistExactMatch(c.ArtistName, cleanArtist);
                bool durOk = expectedDurationSec <= 1 || c.DurationSec <= 1 || Math.Abs(c.DurationSec - expectedDurationSec) <= 15.0;
                bool durTight = expectedDurationSec > 1 && c.DurationSec > 1 && Math.Abs(c.DurationSec - expectedDurationSec) <= 4.5;

                int rank = (c.Source, c.IsSynced) switch {
                    ("LRCLIB", true) => 1,
                    ("Netease", true) => 2,
                    ("LRCLIB", false) => 3,
                    ("Netease", false) => 4,
                    ("Lyrics.ovh", _) => 5,
                    _ => 6
                };

                int score = (titleMatch ? 500 : (NormalizeCompare(c.TrackName).Contains(normTitle) ? 180 : 0))
                    + (artistMatch ? 400 : 0)
                    + (c.IsSynced ? 220 : 0)
                    + (durTight ? 150 : (durOk ? 30 : -200))
                    - (rank - 1) * 40;

                bool exact = titleMatch && artistMatch && durOk;
                return new OnlineLyricsCandidate {
                    TrackName = c.TrackName,
                    ArtistName = c.ArtistName,
                    AlbumName = c.AlbumName,
                    Source = c.IsSynced ? $"{c.Source} Synced" : c.Source,
                    IsSynced = c.IsSynced,
                    SyncedLrc = c.SyncedLrc,
                    PlainLyrics = c.PlainLyrics,
                    DurationSec = c.DurationSec,
                    SourceRank = rank,
                    MatchScore = score,
                    IsExactMatch = exact
                };
            })
            .OrderByDescending(c => c.IsExactMatch)
            .ThenByDescending(c => c.IsSynced)
            .ThenBy(c => c.SourceRank)
            .ThenByDescending(c => c.MatchScore)
            .ToList();
    }

    static OnlineLyricsCandidate ParseLrcLibItem(JsonElement el) {
        string synced = el.TryGetProperty("syncedLyrics", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String
            ? (sEl.GetString() ?? "")
            : "";
        string plain = el.TryGetProperty("plainLyrics", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String
            ? (pEl.GetString() ?? "")
            : "";
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain)) return null;

        return new OnlineLyricsCandidate {
            TrackName = el.TryGetProperty("trackName", out JsonElement tEl) ? (tEl.GetString() ?? "") : "",
            ArtistName = el.TryGetProperty("artistName", out JsonElement arEl) ? (arEl.GetString() ?? "") : "",
            AlbumName = el.TryGetProperty("albumName", out JsonElement alEl) ? (alEl.GetString() ?? "") : "",
            Source = "LRCLIB",
            IsSynced = !string.IsNullOrWhiteSpace(synced),
            SyncedLrc = synced,
            PlainLyrics = plain,
            DurationSec = el.TryGetProperty("duration", out JsonElement dEl) && dEl.TryGetDouble(out double d) ? d : 0
        };
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

                string lyrUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
                using HttpRequestMessage lyrReq = new(HttpMethod.Get, lyrUrl);
                lyrReq.Headers.Referrer = new Uri("https://music.163.com/");
                using HttpResponseMessage lyrResp = await http.SendAsync(lyrReq, ct).ConfigureAwait(false);
                if (!lyrResp.IsSuccessStatusCode) continue;

                string lyrJson = await lyrResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument lyrDoc = JsonDocument.Parse(lyrJson);
                string lrc = lyrDoc.RootElement.TryGetProperty("lrc", out JsonElement lrcObj)
                    && lrcObj.TryGetProperty("lyric", out JsonElement lrcStr)
                    ? (lrcStr.GetString() ?? "")
                    : "";
                if (string.IsNullOrWhiteSpace(lrc)) continue;

                bool hasTimestamp = lrc.Contains('[') && lrc.Contains(':');
                results.Add(new OnlineLyricsCandidate {
                    TrackName = songName,
                    ArtistName = artistName,
                    AlbumName = albName,
                    Source = "Netease",
                    IsSynced = hasTimestamp,
                    SyncedLrc = hasTimestamp ? lrc : "",
                    PlainLyrics = hasTimestamp ? "" : lrc,
                    DurationSec = durSec
                });
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.Netease: {ex.Message}");
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
                req.Headers.UserAgent.ParseAdd("TemporaryUserAgent");
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
                req.Headers.UserAgent.ParseAdd("TemporaryUserAgent");
            }
            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length < 16) return null;

            string dir = GetCacheSubdir(ctx, "subtitles");
            string safeBase = HashKey($"{candidate.Id}|{candidate.Title}");

            // GZip archive starts with 0x1F, 0x8B (OpenSubtitles SubDownloadLink)
            if (bytes[0] == 0x1F && bytes[1] == 0x8B) {
                using MemoryStream ms = new(bytes);
                using GZipStream gz = new(ms, CompressionMode.Decompress);
                string outPath = IOPath.Combine(dir, safeBase + ".srt");
                using FileStream fs = IOFile.Create(outPath);
                await gz.CopyToAsync(fs, ct).ConfigureAwait(false);
                return outPath;
            }

            // ZIP archive starts with 'PK' (0x50, 0x4B)
            if (bytes[0] == 0x50 && bytes[1] == 0x4B) {
                using MemoryStream ms = new(bytes);
                using ZipArchive zip = new(ms, ZipArchiveMode.Read);
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".ass", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                string ext = IOPath.GetExtension(entry.Name).ToLowerInvariant();
                string outPath = IOPath.Combine(dir, safeBase + ext);
                using Stream es = entry.Open();
                using FileStream fs = IOFile.Create(outPath);
                await es.CopyToAsync(fs, ct).ConfigureAwait(false);
                return outPath;
            }

            string rawPath = IOPath.Combine(dir, safeBase + ".srt");
            await IOFile.WriteAllBytesAsync(rawPath, bytes, ct).ConfigureAwait(false);
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
        bool needLyrics = !meta.HasVideo && !(meta.Lyrics?.HasLyrics ?? false);
        if (!needCover && !needLyrics) return null;

        Bitmap fetchedArt = null;
        string artSource = "";
        LyricsDocument fetchedLyrics = null;
        string lyrSource = "";

        Task coverTask = Task.Run(async () => {
            if (!needCover) return;
            List<OnlineCoverCandidate> covers = await SearchCoverCandidatesAsync(
                meta.Title, meta.Artist, meta.Album, meta.HasVideo, ct).ConfigureAwait(false);
            // When auto-enriching silently, prefer exact matches first if available
            IEnumerable<OnlineCoverCandidate> picks = covers.Any(c => c.IsExactMatch)
                ? covers.Where(c => c.IsExactMatch).Take(2)
                : covers.Take(2);
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
            OnlineLyricsCandidate best = cands.FirstOrDefault(c => c.IsExactMatch) ?? cands.FirstOrDefault();
            if (best != null && !string.IsNullOrWhiteSpace(best.BestContent)) {
                SaveLyricsToCache(ctx, meta.Title, meta.Artist, best.BestContent);
                LyricsDocument doc = LyricsService.ParseLyricsText(best.BestContent, best.Source);
                if (doc.HasLyrics) {
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
