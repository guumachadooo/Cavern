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
}

public sealed class OnlineLyricsCandidate {
    public string TrackName { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public bool IsSynced { get; init; }
    public string SyncedLrc { get; init; } = "";
    public string PlainLyrics { get; init; } = "";
    public double DurationSec { get; init; }
    public string BestContent => !string.IsNullOrWhiteSpace(SyncedLrc) ? SyncedLrc : PlainLyrics;
}

public sealed class OnlineSubtitleCandidate {
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Language { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Source { get; init; } = "";
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

    public static string GetCachedCoverPath(Context ctx, string title, string artist, string album) {
        if (ctx == null) return null;
        string dir = GetCacheSubdir(ctx, "covers");
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album)) {
            string p1 = IOPath.Combine(dir, HashKey($"alb|{artist}|{album}") + ".jpg");
            if (IOFile.Exists(p1)) return p1;
        }
        if (!string.IsNullOrWhiteSpace(title)) {
            string p2 = IOPath.Combine(dir, HashKey($"trk|{artist}|{title}") + ".jpg");
            if (IOFile.Exists(p2)) return p2;
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
        List<OnlineCoverCandidate> results = [];
        string cleanQuery = CleanSearchTerm(query);
        string cleanArtist = CleanSearchTerm(artist);
        string cleanAlbum = CleanSearchTerm(album);
        if (string.IsNullOrWhiteSpace(cleanQuery) && string.IsNullOrWhiteSpace(cleanAlbum)) {
            return results;
        }

        if (isVideo) {
            await TrySearchTmdbAsync(cleanQuery, results, ct).ConfigureAwait(false);
        }

        string searchTerm = string.IsNullOrWhiteSpace(cleanArtist)
            ? cleanQuery
            : $"{cleanArtist} {(string.IsNullOrWhiteSpace(cleanQuery) ? cleanAlbum : cleanQuery)}";

        await TrySearchItunesAsync(searchTerm, results, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(cleanAlbum)) {
            await TrySearchAudioDbAsync(cleanArtist, cleanAlbum, results, ct).ConfigureAwait(false);
            if (results.Count < 4) {
                await TrySearchMusicBrainzAsync(cleanArtist, cleanAlbum, results, ct).ConfigureAwait(false);
            }
        }

        return results
            .GroupBy(r => r.ImageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(10)
            .ToList();
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
        CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(imageUrl)) return (null, null);
        try {
            using HttpResponseMessage resp = await http.GetAsync(imageUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && imageUrl.Contains("1000x1000bb")) {
                string fallbackUrl = imageUrl.Replace("1000x1000bb", "600x600bb");
                using HttpResponseMessage fbResp = await http.GetAsync(fallbackUrl, ct).ConfigureAwait(false);
                if (!fbResp.IsSuccessStatusCode) return (null, null);
                byte[] fbBytes = await fbResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return SaveAndDecodeCoverBytes(ctx, fbBytes, title, artist, album);
            }
            if (!resp.IsSuccessStatusCode) return (null, null);
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return SaveAndDecodeCoverBytes(ctx, bytes, title, artist, album);
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.DownloadCover: {ex.Message}");
            return (null, null);
        }
    }

    static (Bitmap bitmap, string savedPath) SaveAndDecodeCoverBytes(Context ctx, byte[] bytes, string title, string artist, string album) {
        if (bytes == null || bytes.Length < 256) return (null, null);
        Bitmap bmp = MediaMetadataHelper.DecodeScaledBitmap(bytes, 640);
        if (bmp == null) return (null, null);

        string dir = GetCacheSubdir(ctx, "covers");
        string savedPath = IOPath.Combine(dir, HashKey($"trk|{artist}|{title}") + ".jpg");
        IOFile.WriteAllBytes(savedPath, bytes);
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album)) {
            string albPath = IOPath.Combine(dir, HashKey($"alb|{artist}|{album}") + ".jpg");
            IOFile.WriteAllBytes(albPath, bytes);
        }
        MediaLibraryService.InvalidateThumbCache(!string.IsNullOrEmpty(album) ? $"{artist}|{album}" : null);
        return (bmp, savedPath);
    }

    public static async Task<List<OnlineLyricsCandidate>> SearchLyricsAsync(
        string title,
        string artist,
        string album = "",
        CancellationToken ct = default) {
        List<OnlineLyricsCandidate> list = [];
        string cleanTitle = CleanSearchTerm(title);
        string cleanArtist = CleanSearchTerm(artist);
        if (string.IsNullOrWhiteSpace(cleanTitle)) return list;

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
                        if (list.Count >= 8) break;
                    }
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"OnlineMetadataService.LRCLIB: {ex.Message}");
        }

        return list
            .OrderByDescending(c => c.IsSynced)
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
            IsSynced = !string.IsNullOrWhiteSpace(synced),
            SyncedLrc = synced,
            PlainLyrics = plain,
            DurationSec = el.TryGetProperty("duration", out JsonElement dEl) && dEl.TryGetDouble(out double d) ? d : 0
        };
    }

    public static async Task<List<OnlineSubtitleCandidate>> SearchSubtitlesAsync(
        string query,
        string lang = "pb",
        CancellationToken ct = default) {
        List<OnlineSubtitleCandidate> list = [];
        string clean = CleanSearchTerm(query);
        if (string.IsNullOrWhiteSpace(clean)) return list;

        // Podnapisi legacy XML endpoint (no API key required, returns direct download link)
        string[] langs = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
            ? ["en"]
            : ["pb", "pt", "en"];

        foreach (string l in langs) {
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
                        Source = "Podnapisi"
                    });
                    if (list.Count >= 12) break;
                }
                if (list.Count >= 6) break;
            } catch (Exception ex) {
                AppLogger.Warn($"OnlineMetadataService.Subtitles({l}): {ex.Message}");
            }
        }
        return list;
    }

    public static async Task<string> DownloadAndExtractSubtitleAsync(
        Context ctx,
        OnlineSubtitleCandidate candidate,
        CancellationToken ct = default) {
        if (candidate == null || string.IsNullOrEmpty(candidate.DownloadUrl)) return null;
        try {
            using HttpRequestMessage req = new(HttpMethod.Get, candidate.DownloadUrl);
            req.Headers.Accept.Clear();
            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length < 16) return null;

            string dir = GetCacheSubdir(ctx, "subtitles");
            string safeBase = HashKey($"{candidate.Id}|{candidate.Title}");

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
            foreach (OnlineCoverCandidate c in covers.Take(2)) {
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
            List<OnlineLyricsCandidate> cands = await SearchLyricsAsync(meta.Title, meta.Artist, meta.Album, ct).ConfigureAwait(false);
            OnlineLyricsCandidate best = cands.FirstOrDefault();
            if (best != null && !string.IsNullOrWhiteSpace(best.BestContent)) {
                SaveLyricsToCache(ctx, meta.Title, meta.Artist, best.BestContent);
                LyricsDocument doc = LyricsService.ParseLyricsText(best.BestContent, best.IsSynced ? "LRCLIB Synced" : "LRCLIB");
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
