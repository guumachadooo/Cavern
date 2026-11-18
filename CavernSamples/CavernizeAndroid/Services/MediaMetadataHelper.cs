using Android.Content;
using Android.Graphics;
using Android.Media;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Extracted embedded media tags, album artwork bitmap, and balanced UI accent palette for a loaded file.
/// </summary>
public sealed class MediaArtworkMetadata {
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string MimeType { get; init; } = "";
    public string SourceMediaPath { get; init; } = "";
    public int EmbeddedSampleRate { get; init; }
    public int EmbeddedBitrateKbps { get; init; }
    public bool HasVideo { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public bool HasEmbeddedArtwork { get; init; }
    public Bitmap ArtworkBitmap { get; init; }
    public Color PrimaryAccent { get; init; }
    public Color SecondaryAccent { get; init; }
    public LyricsDocument Lyrics { get; init; }
}

/// <summary>
/// Reads embedded audio/video tags, video dimensions/thumbnails, cover art via Android's
/// <see cref="MediaMetadataRetriever"/>, and adjacent/embedded lyrics via <see cref="LyricsService"/>.
/// </summary>
public static class MediaMetadataHelper {
    static readonly string[] ExternalCoverNames = [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.png", "artwork.jpg", "artwork.png"
    ];

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts"
    };

    public static MediaArtworkMetadata Extract(Context context, Android.Net.Uri sourceUri, string resolvedPath, string fallbackTitle) {
        string title = "";
        string artist = "";
        string album = "";
        string mime = "";
        int sampleRate = 0;
        int bitrateKbps = 0;
        bool hasVideo = false;
        int videoWidth = 0;
        int videoHeight = 0;
        Bitmap artwork = null;
        bool hasEmbedded = false;

        using MediaMetadataRetriever retriever = new();
        bool sourceOpened = false;

        if (sourceUri != null && context != null) {
            try {
                retriever.SetDataSource(context, sourceUri);
                sourceOpened = true;
            } catch {
                sourceOpened = false;
            }
        }

        if (!sourceOpened && !string.IsNullOrEmpty(resolvedPath) && IOFile.Exists(resolvedPath)) {
            try {
                retriever.SetDataSource(resolvedPath);
                sourceOpened = true;
            } catch {
                sourceOpened = false;
            }
        }

        if (sourceOpened) {
            try {
                title = (retriever.ExtractMetadata(MetadataKey.Title) ?? "").Trim();
                artist = (retriever.ExtractMetadata(MetadataKey.Artist)
                    ?? retriever.ExtractMetadata(MetadataKey.Albumartist)
                    ?? "").Trim();
                album = (retriever.ExtractMetadata(MetadataKey.Album) ?? "").Trim();
                mime = (retriever.ExtractMetadata(MetadataKey.Mimetype) ?? "").Trim();

                string bitrateRaw = retriever.ExtractMetadata(MetadataKey.Bitrate);
                if (int.TryParse(bitrateRaw, out int bps) && bps > 0) {
                    bitrateKbps = bps / 1000;
                }

                string hasVideoRaw = (retriever.ExtractMetadata(MetadataKey.HasVideo) ?? "").Trim();
                string vwRaw = retriever.ExtractMetadata(MetadataKey.VideoWidth);
                string vhRaw = retriever.ExtractMetadata(MetadataKey.VideoHeight);
                int.TryParse(vwRaw, out videoWidth);
                int.TryParse(vhRaw, out videoHeight);
                hasVideo = "yes".Equals(hasVideoRaw, StringComparison.OrdinalIgnoreCase)
                    || "true".Equals(hasVideoRaw, StringComparison.OrdinalIgnoreCase)
                    || (videoWidth > 0 && videoHeight > 0);

                if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
                    string srRaw = retriever.ExtractMetadata((MetadataKey)38); // METADATA_KEY_SAMPLERATE
                    if (int.TryParse(srRaw, out int sr) && sr > 0) {
                        sampleRate = sr;
                    }
                }

                byte[] embeddedBytes = retriever.GetEmbeddedPicture();
                if (embeddedBytes != null && embeddedBytes.Length > 0) {
                    artwork = DecodeScaledBitmap(embeddedBytes, 640);
                    hasEmbedded = artwork != null;
                }

                if (artwork == null && hasVideo) {
                    using Bitmap rawFrame = retriever.GetFrameAtTime(2_000_000, Option.ClosestSync)
                        ?? retriever.GetFrameAtTime();
                    if (rawFrame != null && !rawFrame.IsRecycled && rawFrame.Width > 0 && rawFrame.Height > 0) {
                        artwork = ScaleBitmapToMax(rawFrame, 640);
                        hasEmbedded = artwork != null;
                    }
                }
            } catch (Exception ex) {
                AppLogger.Error("MediaMetadataHelper.Extract", ex);
            }
        }

        if (!hasVideo && !string.IsNullOrEmpty(resolvedPath)) {
            string ext = IOPath.GetExtension(resolvedPath);
            if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext) && !"audio/mp4".Equals(mime, StringComparison.OrdinalIgnoreCase)) {
                hasVideo = !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (artwork == null && !string.IsNullOrEmpty(resolvedPath)) {
            artwork = TryLoadAdjacentFolderCover(context, sourceUri, resolvedPath, 640);
            hasEmbedded = artwork != null;
        }

        string effectiveTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : CleanTitleFromFilename(fallbackTitle);

        Color primary;
        Color secondary;
        if (artwork != null) {
            ExtractPalette(artwork, out primary, out secondary);
        } else {
            DeriveProceduralPalette(effectiveTitle, out primary, out secondary);
            artwork = CreateSpatialFallbackArtwork(effectiveTitle, primary, secondary, 512);
        }

        LyricsDocument lyrics = LyricsService.LoadForMedia(context, sourceUri, resolvedPath, effectiveTitle);

        return new MediaArtworkMetadata {
            Title = effectiveTitle,
            Artist = artist,
            Album = album,
            MimeType = mime,
            SourceMediaPath = resolvedPath ?? "",
            EmbeddedSampleRate = sampleRate,
            EmbeddedBitrateKbps = bitrateKbps,
            HasVideo = hasVideo,
            VideoWidth = videoWidth,
            VideoHeight = videoHeight,
            HasEmbeddedArtwork = hasEmbedded,
            ArtworkBitmap = artwork,
            PrimaryAccent = primary,
            SecondaryAccent = secondary,
            Lyrics = lyrics
        };
    }

    public static Bitmap CreateSpatialFallbackArtwork(string seedTitle, Color primaryAccent, Color secondaryAccent, int size = 512) {
        Bitmap bmp = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!);
        using Canvas canvas = new(bmp);

        uint hash = StableHash(seedTitle ?? "Cavernize");
        float cx = size * (0.36f + ((hash & 0xFF) / 255f) * 0.28f);
        float cy = size * (0.32f + (((hash >> 8) & 0xFF) / 255f) * 0.24f);

        using Paint bgPaint = new(PaintFlags.AntiAlias);
        bgPaint.SetShader(new LinearGradient(
            0f, 0f, size, size,
            [Color.Rgb(15, 17, 24), Color.Rgb(24, 27, 37), Color.Rgb(12, 14, 20)],
            [0f, 0.52f, 1f],
            Shader.TileMode.Clamp!));
        canvas.DrawRect(0, 0, size, size, bgPaint);

        using Paint primaryGlow = new(PaintFlags.AntiAlias);
        primaryGlow.SetShader(new RadialGradient(
            cx, cy, size * 0.72f,
            [
                Color.Argb(185, primaryAccent.R, primaryAccent.G, primaryAccent.B),
                Color.Argb(58, primaryAccent.R, primaryAccent.G, primaryAccent.B),
                Color.Transparent
            ],
            [0f, 0.48f, 1f],
            Shader.TileMode.Clamp!));
        canvas.DrawRect(0, 0, size, size, primaryGlow);

        using Paint secondaryGlow = new(PaintFlags.AntiAlias);
        secondaryGlow.SetShader(new RadialGradient(
            size - cx * 0.65f, size * 0.78f, size * 0.62f,
            [
                Color.Argb(140, secondaryAccent.R, secondaryAccent.G, secondaryAccent.B),
                Color.Transparent
            ],
            null,
            Shader.TileMode.Clamp!));
        canvas.DrawRect(0, 0, size, size, secondaryGlow);

        float center = size * 0.5f;
        using Paint ringPaint = new(PaintFlags.AntiAlias);
        ringPaint.SetStyle(Paint.Style.Stroke);
        for (int i = 1; i <= 4; i++) {
            float radius = size * (0.12f + i * 0.088f);
            ringPaint.StrokeWidth = size * (i == 2 ? 0.0045f : 0.0025f);
            ringPaint.Color = Color.Argb(28 + (4 - i) * 14, 255, 255, 255);
            canvas.DrawCircle(center, center, radius, ringPaint);
        }

        // Crisp center spatial medallion
        using Paint discPaint = new(PaintFlags.AntiAlias);
        discPaint.Color = Color.Argb(44, 255, 255, 255);
        canvas.DrawCircle(center, center, size * 0.145f, discPaint);

        using Paint discStroke = new(PaintFlags.AntiAlias);
        discStroke.SetStyle(Paint.Style.Stroke);
        discStroke.StrokeWidth = size * 0.0035f;
        discStroke.Color = Color.Argb(105, 255, 255, 255);
        canvas.DrawCircle(center, center, size * 0.145f, discStroke);

        // Minimalist C / spatial arc emblem
        using Paint emblemPaint = new(PaintFlags.AntiAlias);
        emblemPaint.SetStyle(Paint.Style.Stroke);
        emblemPaint.StrokeCap = Paint.Cap.Round;
        emblemPaint.StrokeWidth = size * 0.022f;
        emblemPaint.Color = Color.Argb(240, 255, 255, 255);
        float arcR = size * 0.068f;
        RectF oval = new(center - arcR, center - arcR, center + arcR, center + arcR);
        canvas.DrawArc(oval, 42f, 276f, false, emblemPaint);

        using Paint coreDot = new(PaintFlags.AntiAlias);
        coreDot.Color = Color.Argb(235, primaryAccent.R, primaryAccent.G, primaryAccent.B);
        canvas.DrawCircle(center + arcR * 0.42f, center, size * 0.017f, coreDot);

        return bmp;
    }

    static Bitmap TryLoadAdjacentFolderCover(Context context, Android.Net.Uri sourceUri, string resolvedPath, int maxDim) {
        try {
            string dir = AndroidFileHelper.TryGetOriginalDirectory(context, sourceUri, resolvedPath);
            if (string.IsNullOrEmpty(dir)) {
                return null;
            }

            foreach (string candidate in ExternalCoverNames) {
                string full = IOPath.Combine(dir, candidate);
                if (IOFile.Exists(full)) {
                    byte[] bytes = IOFile.ReadAllBytes(full);
                    Bitmap bmp = DecodeScaledBitmap(bytes, maxDim);
                    if (bmp != null) {
                        return bmp;
                    }
                }
            }
        } catch {
            // Optional fallback cover only
        }
        return null;
    }

    static Bitmap ScaleBitmapToMax(Bitmap source, int maxDim) {
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maxDim) {
            return source.Copy(Bitmap.Config.Argb8888!, false);
        }
        float scale = maxDim / (float)longest;
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        return Bitmap.CreateScaledBitmap(source, w, h, true);
    }

    static Bitmap DecodeScaledBitmap(byte[] bytes, int maxDim) {
        BitmapFactory.Options bounds = new() { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
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
        Bitmap decoded = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, opts);
        if (decoded == null) {
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
            decoded.Recycle();
        }
        return scaled;
    }

    static void ExtractPalette(Bitmap source, out Color primary, out Color secondary) {
        Bitmap tiny = Bitmap.CreateScaledBitmap(source, 24, 24, true);
        try {
            double bestScore = -1;
            Color bestColor = Color.Rgb(118, 168, 255);
            double secondScore = -1;
            Color secondColor = Color.Rgb(158, 134, 245);
            float bestHue = 220f;

            for (int y = 2; y < 22; y += 2) {
                for (int x = 2; x < 22; x += 2) {
                    Color c = new(tiny.GetPixel(x, y));
                    RgbToHsv(c.R, c.G, c.B, out float h, out float s, out float v);
                    if (v < 0.14f || v > 0.96f) {
                        continue;
                    }
                    double score = s * (1.0 - Math.Abs(v - 0.58f)) + 0.05;
                    if (score > bestScore) {
                        secondScore = bestScore;
                        secondColor = bestColor;
                        bestScore = score;
                        bestColor = c;
                        bestHue = h;
                    } else if (score > secondScore && Math.Abs(h - bestHue) > 28f) {
                        secondScore = score;
                        secondColor = c;
                    }
                }
            }

            primary = NormalizeAccentForDarkUi(bestColor);
            if (secondScore > 0) {
                secondary = NormalizeAccentForDarkUi(secondColor);
            } else {
                RgbToHsv(primary.R, primary.G, primary.B, out float h, out float s, out float v);
                secondary = HsvToColor((h + 38f) % 360f, Math.Max(0.35f, s * 0.9f), Math.Clamp(v * 0.92f, 0.58f, 0.88f));
            }
        } finally {
            if (!ReferenceEquals(tiny, source)) {
                tiny.Recycle();
            }
        }
    }

    static Color NormalizeAccentForDarkUi(Color color) {
        RgbToHsv(color.R, color.G, color.B, out float h, out float s, out float v);
        float targetS = s < 0.14f ? 0.18f : Math.Clamp(s, 0.36f, 0.82f);
        float targetV = Math.Clamp(Math.Max(v, 0.68f), 0.68f, 0.92f);
        return HsvToColor(h, targetS, targetV);
    }

    static void DeriveProceduralPalette(string seed, out Color primary, out Color secondary) {
        uint hash = StableHash(seed);
        float hue = 195f + ((hash % 150) - 35f);
        if (hue < 0f) {
            hue += 360f;
        }
        primary = HsvToColor(hue % 360f, 0.56f, 0.86f);
        secondary = HsvToColor((hue + 44f) % 360f, 0.52f, 0.78f);
    }

    static string CleanTitleFromFilename(string rawName) {
        if (string.IsNullOrWhiteSpace(rawName)) {
            return "Cavernize";
        }
        try {
            return IOPath.GetFileNameWithoutExtension(rawName);
        } catch {
            return rawName;
        }
    }

    static uint StableHash(string text) {
        uint h = 2166136261u;
        foreach (char c in text ?? "") {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }

    static void RgbToHsv(int r8, int g8, int b8, out float h, out float s, out float v) {
        float r = r8 / 255f;
        float g = g8 / 255f;
        float b = b8 / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        v = max;
        s = max <= 0.0001f ? 0f : delta / max;

        if (delta <= 0.0001f) {
            h = 0f;
        } else if (Math.Abs(max - r) < 0.0001f) {
            h = 60f * (((g - b) / delta) % 6f);
        } else if (Math.Abs(max - g) < 0.0001f) {
            h = 60f * ((b - r) / delta + 2f);
        } else {
            h = 60f * ((r - g) / delta + 4f);
        }
        if (h < 0f) {
            h += 360f;
        }
    }

    static Color HsvToColor(float h, float s, float v) {
        float c = v * s;
        float x = c * (1f - Math.Abs(((h / 60f) % 2f) - 1f));
        float m = v - c;
        float rf, gf, bf;

        if (h < 60f) {
            rf = c; gf = x; bf = 0f;
        } else if (h < 120f) {
            rf = x; gf = c; bf = 0f;
        } else if (h < 180f) {
            rf = 0f; gf = c; bf = x;
        } else if (h < 240f) {
            rf = 0f; gf = x; bf = c;
        } else if (h < 300f) {
            rf = x; gf = 0f; bf = c;
        } else {
            rf = c; gf = 0f; bf = x;
        }

        int r = Math.Clamp((int)Math.Round((rf + m) * 255f), 0, 255);
        int g = Math.Clamp((int)Math.Round((gf + m) * 255f), 0, 255);
        int b = Math.Clamp((int)Math.Round((bf + m) * 255f), 0, 255);
        return Color.Rgb(r, g, b);
    }
}
