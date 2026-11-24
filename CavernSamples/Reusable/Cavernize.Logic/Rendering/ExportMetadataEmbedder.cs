using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Cavern.Spatial;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Full metadata, cover art, and synchronized lyrics payload preserved and embedded across Studio &amp; Classic exports.
/// </summary>
public sealed class ExportMetadataPackage {
    /// <summary>Track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Primary performing artist.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Album artist (falls back to <see cref="Artist"/> when omitted).</summary>
    public string AlbumArtist { get; set; } = string.Empty;

    /// <summary>Album or release name.</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>1-based track number (0 = unset).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Total tracks in disc/album (0 = unset).</summary>
    public int TrackTotal { get; set; }

    /// <summary>1-based disc number (0 = unset).</summary>
    public int DiscNumber { get; set; }

    /// <summary>Release year or ISO date (e.g. "2024").</summary>
    public string Year { get; set; } = string.Empty;

    /// <summary>Music genre.</summary>
    public string Genre { get; set; } = string.Empty;

    /// <summary>Export comment / mastering note.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Encoder identification string.</summary>
    public string Encoder { get; set; } = "Cavernize";

    /// <summary>When true, embeds <see cref="CoverArtBytes"/> into supported containers.</summary>
    public bool EmbedCoverArt { get; set; } = true;

    /// <summary>Compressed JPEG or PNG front cover bytes (recommended &lt;= 256 KB).</summary>
    public byte[] CoverArtBytes { get; set; }

    /// <summary>MIME type for <see cref="CoverArtBytes"/> (e.g. <c>image/jpeg</c> or <c>image/png</c>).</summary>
    public string CoverArtMimeType { get; set; } = "image/jpeg";

    /// <summary>Cover image pixel width (0 = auto-detect from JPEG/PNG header).</summary>
    public int CoverArtWidth { get; set; }

    /// <summary>Cover image pixel height (0 = auto-detect from JPEG/PNG header).</summary>
    public int CoverArtHeight { get; set; }

    /// <summary>When true, embeds synchronized (LRC) or plain lyrics into supported containers.</summary>
    public bool EmbedLyrics { get; set; } = true;

    /// <summary>Synchronized LRC / Enhanced LRC lyrics text.</summary>
    public string SyncedLyricsLrc { get; set; } = string.Empty;

    /// <summary>Unsynced plain lyrics fallback.</summary>
    public string PlainLyrics { get; set; } = string.Empty;

    /// <summary>True when at least one descriptive text tag is populated.</summary>
    public bool HasAnyTextTags =>
        !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Artist) ||
        !string.IsNullOrWhiteSpace(AlbumArtist) ||
        !string.IsNullOrWhiteSpace(Album) ||
        TrackNumber > 0 ||
        DiscNumber > 0 ||
        !string.IsNullOrWhiteSpace(Year) ||
        !string.IsNullOrWhiteSpace(Genre) ||
        !string.IsNullOrWhiteSpace(Comment);

    /// <summary>True when cover embedding is enabled and non-empty image bytes are present.</summary>
    public bool HasCoverArt => EmbedCoverArt && CoverArtBytes != null && CoverArtBytes.Length >= 16;

    /// <summary>Best available lyrics string respecting <see cref="EmbedLyrics"/>.</summary>
    public string EffectiveLyrics => !EmbedLyrics
        ? string.Empty
        : (!string.IsNullOrWhiteSpace(SyncedLyricsLrc) ? SyncedLyricsLrc.Trim() : (PlainLyrics ?? string.Empty).Trim());

    /// <summary>True when lyrics embedding is enabled and non-empty lyrics exist.</summary>
    public bool HasLyrics => !string.IsNullOrWhiteSpace(EffectiveLyrics);

    /// <summary>True when <see cref="EffectiveLyrics"/> contains LRC timestamps.</summary>
    public bool HasSyncedLyrics => EmbedLyrics && !string.IsNullOrWhiteSpace(SyncedLyricsLrc) && SyncedLyricsLrc.Contains('[');

    /// <summary>True when any tags, cover art, or lyrics should be written.</summary>
    public bool HasAnythingToEmbed => HasAnyTextTags || HasCoverArt || HasLyrics;

    /// <summary>Creates a shallow copy safe to reuse across batch export jobs.</summary>
    public ExportMetadataPackage Clone() => new() {
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        TrackNumber = TrackNumber,
        TrackTotal = TrackTotal,
        DiscNumber = DiscNumber,
        Year = Year,
        Genre = Genre,
        Comment = Comment,
        Encoder = Encoder,
        EmbedCoverArt = EmbedCoverArt,
        CoverArtBytes = CoverArtBytes,
        CoverArtMimeType = CoverArtMimeType,
        CoverArtWidth = CoverArtWidth,
        CoverArtHeight = CoverArtHeight,
        EmbedLyrics = EmbedLyrics,
        SyncedLyricsLrc = SyncedLyricsLrc,
        PlainLyrics = PlainLyrics
    };
}

/// <summary>
/// Outcome summary returned by <see cref="ExportMetadataEmbedder.EmbedInPlace"/>.
/// </summary>
public sealed class ExportMetadataEmbedResult {
    /// <summary>True when descriptive text tags were written.</summary>
    public bool EmbeddedTags { get; init; }

    /// <summary>True when front cover artwork was written.</summary>
    public bool EmbeddedCoverArt { get; init; }

    /// <summary>True when lyrics (synced or plain) were written.</summary>
    public bool EmbeddedLyrics { get; init; }

    /// <summary>Human-readable diagnostic note describing container handling.</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Clean, human-readable, filesystem-safe filename builder for Studio and Classic exports.
/// Eliminates raw cache filenames, rip bracket noise, and ugly enum suffix concatenation.
/// </summary>
public static partial class ExportFileNameFormatter {
    const int MaxStemLength = 116;

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^\d{1,3}\s*[-._]\s+(?=[A-Za-zÀ-ÿ])", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackPrefixRegex();

    [GeneratedRegex(@"(?:[\[\(][^\[\]\(\)]*\b(?:\d+\s*bit|\d+(?:\.\d+)?\s*k?hz|flac|alac|wav|opus|atmos|dolby|truehd|eac3|web-?dl|lossless|hi-?res)\b[^\[\]\(\)]*[\]\)])", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TechBracketNoiseRegex();

    /// <summary>
    /// Formats a clean, human-readable filename for a Studio composable export, e.g.
    /// <c>Daft Punk - Get Lucky (Dolby Atmos ADM - AI - Motion Orbit).wav</c>.
    /// </summary>
    public static string FormatStudioFileName(
        ExportMetadataPackage metadata,
        string fallbackFilePath,
        StudioBaseOutputFormat baseFormat,
        StudioCompressionTier tier,
        bool enableAiUpmix,
        bool enableKaraoke,
        bool removeBackingVocals,
        bool includePerformance,
        bool enableLosslessTwin,
        SpatialChoreoMode dynamicChoreoMode,
        string extensionWithDot) {
        string ext = NormalizeExtension(extensionWithDot);
        string baseStem = BuildCleanTitleStem(metadata, fallbackFilePath);

        string formatTag = baseFormat switch {
            StudioBaseOutputFormat.Headphone3D => "3D Headphones",
            StudioBaseOutputFormat.Cinema714 => "7.1.4 Surround",
            StudioBaseOutputFormat.DolbyAtmosAdm => "Dolby Atmos ADM",
            StudioBaseOutputFormat.EclipsaIamf => "Eclipsa 3D",
            StudioBaseOutputFormat.Ambisonics360 => "360 Ambisonics",
            _ => "Spatial Master"
        };

        List<string> badges = [formatTag];
        if (enableAiUpmix) {
            badges.Add("AI");
        }
        if (dynamicChoreoMode != SpatialChoreoMode.Off) {
            badges.Add($"Motion {FormatChoreoLabel(dynamicChoreoMode)}");
        }
        if (enableKaraoke) {
            badges.Add(removeBackingVocals ? "Instrumental" : "Karaoke");
        }
        if (includePerformance) {
            badges.Add("Vocal Take");
        }
        if (enableLosslessTwin) {
            badges.Add("Master Twin");
        }
        if (tier == StudioCompressionTier.PortableLossy && baseFormat == StudioBaseOutputFormat.EclipsaIamf) {
            badges.Add("Portable");
        }

        string suffix = $" ({string.Join(" - ", badges)})";
        string stem = CombineAndTruncateStem(baseStem, suffix, MaxStemLength);
        return stem + ext;
    }

    /// <summary>
    /// Formats a clean, human-readable filename for Classic Studio exports.
    /// </summary>
    public static string FormatClassicFileName(
        ExportMetadataPackage metadata,
        string fallbackFilePath,
        string targetShortName,
        string extensionWithDot) {
        string ext = NormalizeExtension(extensionWithDot);
        string baseStem = BuildCleanTitleStem(metadata, fallbackFilePath);
        string cleanTarget = SanitizeSegment(targetShortName);
        string suffix = string.IsNullOrWhiteSpace(cleanTarget) ? " (Cavernize)" : $" ({cleanTarget})";
        return CombineAndTruncateStem(baseStem, suffix, MaxStemLength) + ext;
    }

    /// <summary>
    /// Resolves a non-colliding file path inside <paramref name="directory"/> using clean <c>(2)</c>, <c>(3)</c> numbering.
    /// </summary>
    public static string ResolveUniqueOutputPath(string directory, string cleanFileName) {
        string dir = string.IsNullOrWhiteSpace(directory) ? "." : directory;
        string name = string.IsNullOrWhiteSpace(cleanFileName) ? "Cavernize Master.wav" : Path.GetFileName(cleanFileName);
        string candidate = Path.Combine(dir, name);
        if (!File.Exists(candidate)) {
            return candidate;
        }
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        int counter = 2;
        while (File.Exists(candidate) && counter < 9999) {
            candidate = Path.Combine(dir, $"{stem} ({counter++}){ext}");
        }
        return candidate;
    }

    /// <summary>
    /// Builds a clean <c>"Artist - Title"</c> or <c>"Title"</c> stem from tags or cleaned source filename.
    /// </summary>
    public static string BuildCleanTitleStem(ExportMetadataPackage metadata, string fallbackFilePath) {
        string artist = SanitizeSegment(metadata?.Artist);
        string title = SanitizeSegment(metadata?.Title);
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title)) {
            // Avoid duplicating "Artist - Artist - Title" when title already starts with "Artist -"
            if (title.StartsWith(artist + " -", StringComparison.OrdinalIgnoreCase)) {
                return title;
            }
            return $"{artist} - {title}";
        }
        if (!string.IsNullOrWhiteSpace(title)) {
            return title;
        }

        string rawFallback = string.Empty;
        if (!string.IsNullOrWhiteSpace(fallbackFilePath)) {
            try {
                rawFallback = Path.GetFileNameWithoutExtension(fallbackFilePath) ?? string.Empty;
            } catch {
                rawFallback = fallbackFilePath;
            }
        }
        if (!string.IsNullOrWhiteSpace(rawFallback)) {
            if (IsOpaqueNumericName(rawFallback.Trim())) {
                return "Cavernize Master";
            }
            if (rawFallback.Contains('_')) {
                rawFallback = rawFallback.Replace('_', ' ');
            }
            rawFallback = TechBracketNoiseRegex().Replace(rawFallback, " ");
            rawFallback = LeadingTrackPrefixRegex().Replace(rawFallback.Trim(), "");
            string cleaned = SanitizeSegment(rawFallback);
            if (!string.IsNullOrWhiteSpace(cleaned) && !IsOpaqueNumericName(cleaned)) {
                return cleaned;
            }
        }
        return "Cavernize Master";
    }

    static bool IsOpaqueNumericName(string value) {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.StartsWith("cavern_input_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("cavern input ", StringComparison.OrdinalIgnoreCase)) return true;
        for (int i = 0; i < value.Length; i++) {
            if (!char.IsDigit(value[i])) {
                return false;
            }
        }
        return value.Length >= 5;
    }

    static string FormatChoreoLabel(SpatialChoreoMode mode) => mode switch {
        SpatialChoreoMode.Subtle => "Subtle",
        SpatialChoreoMode.Musical => "Musical",
        SpatialChoreoMode.Cinematic => "Cinematic",
        SpatialChoreoMode.Orbit => "Orbit",
        _ => "Static"
    };

    /// <summary>
    /// Strips invalid path/filesystem characters, traversal dots, control chars, and redundant whitespace.
    /// </summary>
    public static string SanitizeSegment(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return string.Empty;
        }
        string trimmed = raw.Trim();
        if ("<unknown>".Equals(trimmed, StringComparison.OrdinalIgnoreCase)) {
            return string.Empty;
        }

        StringBuilder sb = new(trimmed.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < trimmed.Length; i++) {
            char c = trimmed[i];
            if (char.IsControl(c) || c == '/' || c == '\\' || c == ':' || c == '*' ||
                c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ||
                Array.IndexOf(invalid, c) >= 0) {
                sb.Append(' ');
            } else {
                sb.Append(c);
            }
        }
        string cleaned = MultiSpaceRegex().Replace(sb.ToString(), " ").Trim(' ', '.', '-');
        while (cleaned.Contains("..", StringComparison.Ordinal)) {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }
        return cleaned.Trim(' ', '.', '-');
    }

    static string CombineAndTruncateStem(string baseStem, string suffix, int maxLen) {
        string safeBase = string.IsNullOrWhiteSpace(baseStem) ? "Cavernize Master" : baseStem;
        string safeSuffix = suffix ?? string.Empty;
        if (safeBase.Length + safeSuffix.Length <= maxLen) {
            return safeBase + safeSuffix;
        }
        int availBase = Math.Max(16, maxLen - safeSuffix.Length);
        string truncated = safeBase.Length > availBase
            ? safeBase[..availBase].TrimEnd(' ', '.', '-', '_')
            : safeBase;
        if (string.IsNullOrWhiteSpace(truncated)) {
            truncated = "Cavernize Master";
        }
        return truncated + safeSuffix;
    }

    static string NormalizeExtension(string ext) {
        if (string.IsNullOrWhiteSpace(ext)) {
            return ".wav";
        }
        string clean = ext.Trim();
        return clean.StartsWith('.') ? clean.ToLowerInvariant() : "." + clean.ToLowerInvariant();
    }
}

/// <summary>
/// Pure-C# lossless metadata, cover art, and synchronized lyrics injector for FLAC, RIFF/RF64 WAV (including ADM BWF &amp; AMB),
/// plus <c>FFMETADATA1</c> generator for FFmpeg streaming pipes (<c>.opus</c>, <c>.wv</c>, <c>.m4a</c>).
/// Never re-encodes audio frames and never corrupts ITU-R BS.2076 <c>axml</c>/<c>chna</c>/<c>dbmd</c> chunks.
/// </summary>
public static partial class ExportMetadataEmbedder {
    /// <summary>Maximum embedded cover art payload size (256 KB) to protect container headers and RAM budgets.</summary>
    public const int MaxCoverArtBytes = 256 * 1024;

    /// <summary>Maximum Opus Vorbis <c>METADATA_BLOCK_PICTURE</c> raw image payload (180 KB before Base64).</summary>
    public const int MaxOpusCoverArtBytes = 180 * 1024;

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled)]
    private static partial Regex InlineWordTagRegex();

    /// <summary>
    /// Losslessly embeds tags, optional cover art, and optional synchronized lyrics into an already-rendered file
    /// based on its container format (<c>.flac</c>, <c>.wav</c>, <c>.amb</c>). Leaves <c>.iamf</c> OBU bitstreams untouched.
    /// </summary>
    public static ExportMetadataEmbedResult EmbedInPlace(string filePath, ExportMetadataPackage metadata) {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || metadata == null || !metadata.HasAnythingToEmbed) {
            return new ExportMetadataEmbedResult { Note = "No metadata to embed." };
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".flac") {
            return EmbedFlacInPlace(filePath, metadata);
        }
        if (ext is ".wav" or ".amb") {
            return EmbedRiffWaveInPlace(filePath, metadata);
        }
        if (ext == ".iamf") {
            return new ExportMetadataEmbedResult { Note = "IAMF v1.0 raw OBU preserves mixLabel without external ID3 injection." };
        }
        return new ExportMetadataEmbedResult { Note = $"Container {ext} uses encoder-pass metadata." };
    }

    /// <summary>
    /// Injects RFC 9639 <c>VORBIS_COMMENT</c> (type 4) and optional <c>PICTURE</c> (type 6) blocks into a FLAC file
    /// while streaming existing audio frames verbatim (0 audio re-encode).
    /// </summary>
    public static ExportMetadataEmbedResult EmbedFlacInPlace(string flacPath, ExportMetadataPackage metadata) {
        byte[] streamInfo;
        long audioFramesOffset;

        using (FileStream readFs = new(flacPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536)) {
            Span<byte> magic = stackalloc byte[4];
            if (readFs.Read(magic) < 4 || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') {
                return new ExportMetadataEmbedResult { Note = "Not a valid FLAC stream." };
            }

            streamInfo = null;
            Span<byte> blkHdr = stackalloc byte[4];
            while (readFs.Position + 4 <= readFs.Length) {
                if (readFs.Read(blkHdr) < 4) {
                    return new ExportMetadataEmbedResult { Note = "Truncated FLAC metadata header." };
                }
                bool isLast = (blkHdr[0] & 0x80) != 0;
                int blkType = blkHdr[0] & 0x7F;
                int blkLen = (blkHdr[1] << 16) | (blkHdr[2] << 8) | blkHdr[3];
                if (blkLen < 0 || readFs.Position + blkLen > readFs.Length) {
                    return new ExportMetadataEmbedResult { Note = "Invalid FLAC metadata block length." };
                }

                if (blkType == 0 && blkLen == 34 && streamInfo == null) {
                    streamInfo = new byte[34];
                    if (ReadExact(readFs, streamInfo, 34) != 34) {
                        return new ExportMetadataEmbedResult { Note = "Failed to read FLAC STREAMINFO." };
                    }
                } else {
                    readFs.Position += blkLen;
                }

                if (isLast) {
                    break;
                }
            }

            if (streamInfo == null) {
                return new ExportMetadataEmbedResult { Note = "FLAC STREAMINFO block missing." };
            }
            audioFramesOffset = readFs.Position;
        }

        byte[] vorbisBlock = BuildVorbisCommentPayload(metadata, out bool hasText, out bool hasLyrics);
        byte[] pictureBlock = metadata.HasCoverArt && metadata.CoverArtBytes.Length <= MaxCoverArtBytes
            ? BuildFlacPictureBlockBytes(metadata.CoverArtBytes, metadata.CoverArtMimeType, metadata.CoverArtWidth, metadata.CoverArtHeight)
            : null;

        List<(byte type, byte[] payload)> extraBlocks = [];
        if (vorbisBlock != null && vorbisBlock.Length > 0 && vorbisBlock.Length <= 0xFFFFFF) {
            extraBlocks.Add((4, vorbisBlock));
        }
        if (pictureBlock != null && pictureBlock.Length > 0 && pictureBlock.Length <= 0xFFFFFF) {
            extraBlocks.Add((6, pictureBlock));
        }
        if (extraBlocks.Count == 0) {
            return new ExportMetadataEmbedResult { Note = "No FLAC metadata blocks generated." };
        }

        string tempPath = flacPath + $".meta_{Guid.NewGuid():N}.tmp";
        try {
            using (FileStream src = new(flacPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (FileStream dst = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan)) {
                dst.Write("fLaC"u8);

                // STREAMINFO (always block 0, never last when extraBlocks > 0)
                WriteFlacBlockHeader(dst, isLast: false, blockType: 0, streamInfo.Length);
                dst.Write(streamInfo, 0, streamInfo.Length);

                for (int i = 0; i < extraBlocks.Count; i++) {
                    bool isLast = i == extraBlocks.Count - 1;
                    WriteFlacBlockHeader(dst, isLast, extraBlocks[i].type, extraBlocks[i].payload.Length);
                    dst.Write(extraBlocks[i].payload, 0, extraBlocks[i].payload.Length);
                }

                src.Position = audioFramesOffset;
                byte[] copyBuf = new byte[65536];
                int read;
                while ((read = src.Read(copyBuf, 0, copyBuf.Length)) > 0) {
                    dst.Write(copyBuf, 0, read);
                }
                dst.Flush();
            }

            File.Move(tempPath, flacPath, overwrite: true);
            return new ExportMetadataEmbedResult {
                EmbeddedTags = hasText,
                EmbeddedCoverArt = pictureBlock != null,
                EmbeddedLyrics = hasLyrics,
                Note = "FLAC VORBIS_COMMENT/PICTURE injected losslessly."
            };
        } catch {
            TryDeleteQuiet(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Appends RIFF <c>LIST/INFO</c> and <c>id3 </c> (ID3v2.3 with <c>TIT2/TPE1/TPE2/TALB/TRCK/TPOS/TYER/TCON/COMM/USLT/SYLT/APIC</c>)
    /// chunks to a RIFF or RF64 WAV/ADM BWF/AMB file without touching <c>fmt </c>, <c>data</c>, <c>axml</c>, <c>chna</c>, or <c>dbmd</c>.
    /// </summary>
    public static ExportMetadataEmbedResult EmbedRiffWaveInPlace(string wavPath, ExportMetadataPackage metadata) {
        using FileStream fs = new(wavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 65536);
        if (fs.Length < 44) {
            return new ExportMetadataEmbedResult { Note = "WAV file too short for metadata." };
        }

        Span<byte> hdr = stackalloc byte[12];
        if (fs.Read(hdr) < 12) {
            return new ExportMetadataEmbedResult { Note = "Failed to read WAV header." };
        }
        bool isRiff = hdr[0] == 'R' && hdr[1] == 'I' && hdr[2] == 'F' && hdr[3] == 'F';
        bool isRf64 = hdr[0] == 'R' && hdr[1] == 'F' && hdr[2] == '6' && hdr[3] == '4';
        bool isWave = hdr[8] == 'W' && hdr[9] == 'A' && hdr[10] == 'V' && hdr[11] == 'E';
        if ((!isRiff && !isRf64) || !isWave) {
            return new ExportMetadataEmbedResult { Note = "Not a RIFF/RF64 WAVE container." };
        }

        bool hasDs64At12 = false;
        if (isRf64) {
            Span<byte> ds64Id = stackalloc byte[4];
            if (fs.Read(ds64Id) == 4 && ds64Id[0] == 'd' && ds64Id[1] == 's' && ds64Id[2] == '6' && ds64Id[3] == '4') {
                hasDs64At12 = true;
            } else {
                return new ExportMetadataEmbedResult { Note = "RF64 ds64 chunk not at expected offset; skipping append safely." };
            }
        }

        byte[] listInfoPayload = BuildRiffListInfoPayload(metadata, out bool hasListText);
        byte[] id3Payload = BuildId3v23Tag(metadata, out bool hasId3Text, out bool hasCover, out bool hasLyrics);
        if (listInfoPayload.Length == 0 && id3Payload.Length == 0) {
            return new ExportMetadataEmbedResult { Note = "No RIFF/ID3 metadata frames generated." };
        }

        long initialLen = fs.Length;
        long padBefore = initialLen & 1L;
        long addedBytes = padBefore;
        if (listInfoPayload.Length > 0) {
            addedBytes += 8L + listInfoPayload.Length + (listInfoPayload.Length & 1L);
        }
        if (id3Payload.Length > 0) {
            addedBytes += 8L + id3Payload.Length + (id3Payload.Length & 1L);
        }

        long finalLen = initialLen + addedBytes;
        long newRiffSize = finalLen - 8L;
        if (isRiff && newRiffSize > uint.MaxValue) {
            return new ExportMetadataEmbedResult { Note = "Standard RIFF WAV near 4 GB limit; skipped metadata append to protect size field." };
        }

        fs.Seek(0, SeekOrigin.End);
        if (padBefore == 1L) {
            fs.WriteByte(0);
        }

        Span<byte> chunkHdr = stackalloc byte[8];
        if (listInfoPayload.Length > 0) {
            chunkHdr[0] = (byte)'L'; chunkHdr[1] = (byte)'I'; chunkHdr[2] = (byte)'S'; chunkHdr[3] = (byte)'T';
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr.Slice(4, 4), (uint)listInfoPayload.Length);
            fs.Write(chunkHdr);
            fs.Write(listInfoPayload, 0, listInfoPayload.Length);
            if ((listInfoPayload.Length & 1) != 0) {
                fs.WriteByte(0);
            }
        }

        if (id3Payload.Length > 0) {
            chunkHdr[0] = (byte)'i'; chunkHdr[1] = (byte)'d'; chunkHdr[2] = (byte)'3'; chunkHdr[3] = (byte)' ';
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr.Slice(4, 4), (uint)id3Payload.Length);
            fs.Write(chunkHdr);
            fs.Write(id3Payload, 0, id3Payload.Length);
            if ((id3Payload.Length & 1) != 0) {
                fs.WriteByte(0);
            }
        }

        if (isRiff) {
            fs.Position = 4;
            Span<byte> sz32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sz32, (uint)newRiffSize);
            fs.Write(sz32);
        } else if (isRf64 && hasDs64At12) {
            // ds64 layout: offset 12='ds64', offset 16=chunkSize(4), offset 20=riffSize(8)
            fs.Position = 20;
            Span<byte> sz64 = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(sz64, newRiffSize);
            fs.Write(sz64);
        }

        fs.Flush();
        return new ExportMetadataEmbedResult {
            EmbeddedTags = hasListText || hasId3Text,
            EmbeddedCoverArt = hasCover,
            EmbeddedLyrics = hasLyrics,
            Note = isRf64 ? "RF64 LIST/INFO + id3 appended with ds64 size update." : "RIFF LIST/INFO + id3 appended cleanly."
        };
    }

    /// <summary>
    /// Writes an FFmpeg <c>;FFMETADATA1</c> side-file (avoiding OS CLI <c>ARG_MAX</c> limits &amp; escaping bugs)
    /// for <see cref="FfmpegStdinPipeSink"/> (<c>.opus</c>, <c>.wv</c>, <c>.m4a</c>, <c>.flac</c>).
    /// When <paramref name="includeOpusBase64Picture"/> is true and cover art fits within <see cref="MaxOpusCoverArtBytes"/>,
    /// includes RFC 7845 <c>METADATA_BLOCK_PICTURE</c>.
    /// </summary>
    public static string WriteFfmpegMetadataFile(
        ExportMetadataPackage metadata,
        bool includeOpusBase64Picture,
        string tempDirectory = null) {
        if (metadata == null || !metadata.HasAnythingToEmbed) {
            return string.Empty;
        }

        StringBuilder sb = new(1024);
        sb.Append(";FFMETADATA1\n");
        AppendFfMeta(sb, "title", metadata.Title);
        AppendFfMeta(sb, "artist", metadata.Artist);
        AppendFfMeta(sb, "album_artist", !string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? metadata.AlbumArtist : metadata.Artist);
        AppendFfMeta(sb, "album", metadata.Album);
        if (metadata.TrackNumber > 0) {
            string trk = metadata.TrackTotal > 0 ? $"{metadata.TrackNumber}/{metadata.TrackTotal}" : metadata.TrackNumber.ToString(CultureInfo.InvariantCulture);
            AppendFfMeta(sb, "track", trk);
        }
        if (metadata.DiscNumber > 0) {
            AppendFfMeta(sb, "disc", metadata.DiscNumber.ToString(CultureInfo.InvariantCulture));
        }
        AppendFfMeta(sb, "date", metadata.Year);
        AppendFfMeta(sb, "genre", metadata.Genre);
        AppendFfMeta(sb, "comment", metadata.Comment);
        AppendFfMeta(sb, "encoder", string.IsNullOrWhiteSpace(metadata.Encoder) ? "Cavernize" : metadata.Encoder);

        if (metadata.HasLyrics) {
            string lyr = metadata.EffectiveLyrics;
            AppendFfMeta(sb, "lyrics", lyr);
            if (metadata.HasSyncedLyrics) {
                AppendFfMeta(sb, "SYNCEDLYRICS", lyr);
            }
        }

        if (includeOpusBase64Picture && metadata.HasCoverArt && metadata.CoverArtBytes.Length <= MaxOpusCoverArtBytes) {
            byte[] picBlock = BuildFlacPictureBlockBytes(
                metadata.CoverArtBytes,
                metadata.CoverArtMimeType,
                metadata.CoverArtWidth,
                metadata.CoverArtHeight);
            if (picBlock != null && picBlock.Length > 0) {
                AppendFfMeta(sb, "METADATA_BLOCK_PICTURE", Convert.ToBase64String(picBlock));
            }
        }

        string dir = !string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory)
            ? tempDirectory
            : Path.GetTempPath();
        string path = Path.Combine(dir, $".cavern_meta_{Guid.NewGuid():N}.ffmeta");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// Builds an RFC 9639 FLAC <c>PICTURE</c> metadata block payload (also used Base64-encoded in Ogg/Opus <c>METADATA_BLOCK_PICTURE</c>).
    /// </summary>
    public static byte[] BuildFlacPictureBlockBytes(byte[] imageBytes, string mimeType, int width = 0, int height = 0) {
        if (imageBytes == null || imageBytes.Length < 16) {
            return null;
        }
        string resolvedMime = ResolveCoverMimeType(imageBytes, mimeType);
        if (width <= 0 || height <= 0) {
            TryReadImageDimensions(imageBytes, out int w, out int h);
            if (width <= 0) width = w;
            if (height <= 0) height = h;
        }

        byte[] mimeBytes = Encoding.ASCII.GetBytes(resolvedMime);
        byte[] descBytes = Encoding.UTF8.GetBytes("Cover");
        using MemoryStream ms = new(32 + mimeBytes.Length + descBytes.Length + imageBytes.Length);
        WriteBigEndianUInt32(ms, 3); // 3 = Cover (front)
        WriteBigEndianUInt32(ms, (uint)mimeBytes.Length);
        ms.Write(mimeBytes, 0, mimeBytes.Length);
        WriteBigEndianUInt32(ms, (uint)descBytes.Length);
        ms.Write(descBytes, 0, descBytes.Length);
        WriteBigEndianUInt32(ms, (uint)Math.Max(0, width));
        WriteBigEndianUInt32(ms, (uint)Math.Max(0, height));
        WriteBigEndianUInt32(ms, 24); // color depth
        WriteBigEndianUInt32(ms, 0);  // non-indexed
        WriteBigEndianUInt32(ms, (uint)imageBytes.Length);
        ms.Write(imageBytes, 0, imageBytes.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// Detects PNG or JPEG pixel dimensions directly from raw compressed bytes without external libraries.
    /// </summary>
    public static bool TryReadImageDimensions(ReadOnlySpan<byte> bytes, out int width, out int height) {
        width = 0;
        height = 0;
        if (bytes.Length < 24) {
            return false;
        }
        // PNG signature + IHDR
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R') {
            width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
            return width > 0 && height > 0;
        }
        // JPEG SOI + SOF0/SOF1/SOF2
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) {
            int i = 2;
            while (i + 9 < bytes.Length) {
                if (bytes[i] != 0xFF) {
                    i++;
                    continue;
                }
                while (i < bytes.Length && bytes[i] == 0xFF) i++;
                if (i >= bytes.Length) break;
                byte marker = bytes[i++];
                if (marker == 0xD9 || marker == 0xDA) break;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (i + 2 > bytes.Length) break;
                int segLen = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i, 2));
                if (segLen < 2 || i + segLen > bytes.Length) break;
                if (marker is 0xC0 or 0xC1 or 0xC2 && segLen >= 7) {
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 5, 2));
                    return width > 0 && height > 0;
                }
                i += segLen;
            }
        }
        return false;
    }

    static byte[] BuildVorbisCommentPayload(ExportMetadataPackage m, out bool hasText, out bool hasLyrics) {
        List<string> comments = [];
        bool hasTextLocal = false;
        hasLyrics = false;

        void Add(string key, string value) {
            if (!string.IsNullOrWhiteSpace(value)) {
                comments.Add($"{key}={value.Trim()}");
                hasTextLocal = true;
            }
        }

        Add("TITLE", m.Title);
        Add("ARTIST", m.Artist);
        Add("ALBUMARTIST", !string.IsNullOrWhiteSpace(m.AlbumArtist) ? m.AlbumArtist : m.Artist);
        Add("ALBUM", m.Album);
        if (m.TrackNumber > 0) {
            Add("TRACKNUMBER", m.TrackNumber.ToString(CultureInfo.InvariantCulture));
        }
        if (m.TrackTotal > 0) {
            Add("TRACKTOTAL", m.TrackTotal.ToString(CultureInfo.InvariantCulture));
        }
        if (m.DiscNumber > 0) {
            Add("DISCNUMBER", m.DiscNumber.ToString(CultureInfo.InvariantCulture));
        }
        Add("DATE", m.Year);
        Add("GENRE", m.Genre);
        Add("COMMENT", m.Comment);
        if (!string.IsNullOrWhiteSpace(m.Encoder)) {
            comments.Add($"ENCODER={m.Encoder.Trim()}");
        }

        if (m.HasLyrics) {
            string lyr = m.EffectiveLyrics;
            if (m.HasSyncedLyrics) {
                comments.Add($"SYNCEDLYRICS={lyr}");
            }
            comments.Add($"LYRICS={lyr}");
            hasLyrics = true;
        }

        hasText = hasTextLocal;
        if (comments.Count == 0) {
            return Array.Empty<byte>();
        }

        byte[] vendorBytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(m.Encoder) ? "Cavernize" : m.Encoder.Trim());
        using MemoryStream ms = new(256 + comments.Count * 48);
        WriteLittleEndianInt32(ms, vendorBytes.Length);
        ms.Write(vendorBytes, 0, vendorBytes.Length);
        WriteLittleEndianInt32(ms, comments.Count);
        for (int i = 0; i < comments.Count; i++) {
            byte[] cb = Encoding.UTF8.GetBytes(comments[i]);
            WriteLittleEndianInt32(ms, cb.Length);
            ms.Write(cb, 0, cb.Length);
        }
        return ms.ToArray();
    }

    static byte[] BuildRiffListInfoPayload(ExportMetadataPackage m, out bool hasText) {
        bool hasTextLocal = false;
        using MemoryStream ms = new(256);
        ms.Write("INFO"u8);

        void AddSubchunk(string fourCc, string val) {
            if (string.IsNullOrWhiteSpace(val)) return;
            byte[] textBytes = Encoding.UTF8.GetBytes(val.Trim());
            int payloadLen = textBytes.Length + 1; // null-terminated
            ms.Write(Encoding.ASCII.GetBytes(fourCc), 0, 4);
            WriteLittleEndianUInt32(ms, (uint)payloadLen);
            ms.Write(textBytes, 0, textBytes.Length);
            ms.WriteByte(0);
            if ((payloadLen & 1) != 0) {
                ms.WriteByte(0);
            }
            hasTextLocal = true;
        }

        AddSubchunk("INAM", m.Title);
        AddSubchunk("IART", m.Artist);
        AddSubchunk("IPRD", m.Album);
        if (m.TrackNumber > 0) {
            AddSubchunk("ITRK", m.TrackTotal > 0 ? $"{m.TrackNumber}/{m.TrackTotal}" : m.TrackNumber.ToString(CultureInfo.InvariantCulture));
        }
        AddSubchunk("ICRD", m.Year);
        AddSubchunk("IGNR", m.Genre);
        AddSubchunk("ICMT", m.Comment);
        if (!string.IsNullOrWhiteSpace(m.Encoder)) {
            byte[] enc = Encoding.UTF8.GetBytes(m.Encoder.Trim());
            int len = enc.Length + 1;
            ms.Write("ISFT"u8);
            WriteLittleEndianUInt32(ms, (uint)len);
            ms.Write(enc, 0, enc.Length);
            ms.WriteByte(0);
            if ((len & 1) != 0) ms.WriteByte(0);
        }

        hasText = hasTextLocal;
        return ms.Length > 4 ? ms.ToArray() : Array.Empty<byte>();
    }

    static byte[] BuildId3v23Tag(ExportMetadataPackage m, out bool hasText, out bool hasCover, out bool hasLyrics) {
        bool hasTextLocal = false;
        hasCover = false;
        hasLyrics = false;

        using MemoryStream frames = new(1024);

        void WriteTextFrame(string frameId, string value) {
            if (string.IsNullOrWhiteSpace(value)) return;
            byte[] utf16 = Encoding.Unicode.GetBytes(value.Trim());
            int payloadLen = 1 + 2 + utf16.Length; // enc(1=UTF16 BOM) + BOM(FF FE) + chars
            WriteId3v23FrameHeader(frames, frameId, payloadLen);
            frames.WriteByte(1);
            frames.WriteByte(0xFF);
            frames.WriteByte(0xFE);
            frames.Write(utf16, 0, utf16.Length);
            hasTextLocal = true;
        }

        WriteTextFrame("TIT2", m.Title);
        WriteTextFrame("TPE1", m.Artist);
        WriteTextFrame("TPE2", !string.IsNullOrWhiteSpace(m.AlbumArtist) ? m.AlbumArtist : m.Artist);
        WriteTextFrame("TALB", m.Album);
        if (m.TrackNumber > 0) {
            WriteTextFrame("TRCK", m.TrackTotal > 0 ? $"{m.TrackNumber}/{m.TrackTotal}" : m.TrackNumber.ToString(CultureInfo.InvariantCulture));
        }
        if (m.DiscNumber > 0) {
            WriteTextFrame("TPOS", m.DiscNumber.ToString(CultureInfo.InvariantCulture));
        }
        WriteTextFrame("TYER", ExtractYear4(m.Year));
        WriteTextFrame("TCON", m.Genre);
        if (!string.IsNullOrWhiteSpace(m.Encoder)) {
            byte[] encBytes = Encoding.Unicode.GetBytes(m.Encoder.Trim());
            WriteId3v23FrameHeader(frames, "TENC", 3 + encBytes.Length);
            frames.WriteByte(1);
            frames.WriteByte(0xFF);
            frames.WriteByte(0xFE);
            frames.Write(encBytes, 0, encBytes.Length);
        }

        if (!string.IsNullOrWhiteSpace(m.Comment)) {
            byte[] cmt = Encoding.Unicode.GetBytes(m.Comment.Trim());
            // enc(1) + lang("eng") + desc BOM+NUL(4) + text BOM(2) + text
            int len = 1 + 3 + 4 + 2 + cmt.Length;
            WriteId3v23FrameHeader(frames, "COMM", len);
            frames.WriteByte(1);
            frames.Write("eng"u8);
            frames.WriteByte(0xFF); frames.WriteByte(0xFE); frames.WriteByte(0); frames.WriteByte(0);
            frames.WriteByte(0xFF); frames.WriteByte(0xFE);
            frames.Write(cmt, 0, cmt.Length);
            hasTextLocal = true;
        }
        hasText = hasTextLocal;

        if (m.HasLyrics) {
            string lyr = m.EffectiveLyrics;
            byte[] usltText = Encoding.Unicode.GetBytes(lyr);
            int usltLen = 1 + 3 + 4 + 2 + usltText.Length;
            WriteId3v23FrameHeader(frames, "USLT", usltLen);
            frames.WriteByte(1);
            frames.Write("und"u8);
            frames.WriteByte(0xFF); frames.WriteByte(0xFE); frames.WriteByte(0); frames.WriteByte(0);
            frames.WriteByte(0xFF); frames.WriteByte(0xFE);
            frames.Write(usltText, 0, usltText.Length);
            hasLyrics = true;

            if (m.HasSyncedLyrics) {
                byte[] syltPayload = BuildId3SyltPayload(m.SyncedLyricsLrc);
                if (syltPayload.Length > 0) {
                    WriteId3v23FrameHeader(frames, "SYLT", syltPayload.Length);
                    frames.Write(syltPayload, 0, syltPayload.Length);
                }
            }
        }

        if (m.HasCoverArt && m.CoverArtBytes.Length <= MaxCoverArtBytes) {
            string mime = ResolveCoverMimeType(m.CoverArtBytes, m.CoverArtMimeType);
            byte[] mimeBytes = Encoding.ASCII.GetBytes(mime);
            // enc(0=Latin1) + mime + NUL + picType(3=Front) + desc("") + NUL + imageBytes
            int apicLen = 1 + mimeBytes.Length + 1 + 1 + 1 + m.CoverArtBytes.Length;
            WriteId3v23FrameHeader(frames, "APIC", apicLen);
            frames.WriteByte(0);
            frames.Write(mimeBytes, 0, mimeBytes.Length);
            frames.WriteByte(0);
            frames.WriteByte(3); // Front cover
            frames.WriteByte(0); // empty description
            frames.Write(m.CoverArtBytes, 0, m.CoverArtBytes.Length);
            hasCover = true;
        }

        if (frames.Length == 0) {
            return Array.Empty<byte>();
        }

        int bodyLen = (int)frames.Length;
        byte[] tag = new byte[10 + bodyLen];
        tag[0] = (byte)'I';
        tag[1] = (byte)'D';
        tag[2] = (byte)'3';
        tag[3] = 3; // ID3v2.3
        tag[4] = 0;
        tag[5] = 0;
        // Synchsafe 28-bit size
        tag[6] = (byte)((bodyLen >> 21) & 0x7F);
        tag[7] = (byte)((bodyLen >> 14) & 0x7F);
        tag[8] = (byte)((bodyLen >> 7) & 0x7F);
        tag[9] = (byte)(bodyLen & 0x7F);
        Buffer.BlockCopy(frames.GetBuffer(), 0, tag, 10, bodyLen);
        return tag;
    }

    static byte[] BuildId3SyltPayload(string lrcText) {
        if (string.IsNullOrWhiteSpace(lrcText)) {
            return Array.Empty<byte>();
        }
        List<(uint ms, string text)> cues = [];
        string[] lines = lrcText.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            MatchCollection matches = LrcTimestampRegex().Matches(line);
            if (matches.Count == 0) continue;
            string lyricBody = LrcTimestampRegex().Replace(line, string.Empty);
            lyricBody = InlineWordTagRegex().Replace(lyricBody, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(lyricBody)) continue;

            for (int m = 0; m < matches.Count; m++) {
                Match mt = matches[m];
                if (!int.TryParse(mt.Groups[1].Value, out int min)) continue;
                if (!int.TryParse(mt.Groups[2].Value, out int sec)) continue;
                int msFrac = 0;
                if (mt.Groups[3].Success) {
                    string frac = mt.Groups[3].Value;
                    if (frac.Length == 1) msFrac = int.Parse(frac, CultureInfo.InvariantCulture) * 100;
                    else if (frac.Length == 2) msFrac = int.Parse(frac, CultureInfo.InvariantCulture) * 10;
                    else msFrac = int.Parse(frac[..3], CultureInfo.InvariantCulture);
                }
                long totalMs = (long)min * 60_000L + sec * 1000L + msFrac;
                if (totalMs >= 0 && totalMs <= uint.MaxValue) {
                    cues.Add(((uint)totalMs, lyricBody));
                }
            }
        }
        if (cues.Count < 2) {
            return Array.Empty<byte>();
        }
        cues.Sort((a, b) => a.ms.CompareTo(b.ms));

        using MemoryStream ms = new(cues.Count * 40);
        ms.WriteByte(1); // UTF-16 w/ BOM
        ms.Write("und"u8);
        ms.WriteByte(2); // Timestamp format: milliseconds
        ms.WriteByte(1); // Content type: lyrics
        // Empty content descriptor (BOM + UTF-16 NUL)
        ms.WriteByte(0xFF); ms.WriteByte(0xFE); ms.WriteByte(0); ms.WriteByte(0);

        Span<byte> ts4 = stackalloc byte[4];
        for (int i = 0; i < cues.Count; i++) {
            byte[] textUtf16 = Encoding.Unicode.GetBytes(cues[i].text);
            ms.WriteByte(0xFF); ms.WriteByte(0xFE);
            ms.Write(textUtf16, 0, textUtf16.Length);
            ms.WriteByte(0); ms.WriteByte(0);
            BinaryPrimitives.WriteUInt32BigEndian(ts4, cues[i].ms);
            ms.Write(ts4);
        }
        return ms.ToArray();
    }

    static string ExtractYear4(string rawYear) {
        if (string.IsNullOrWhiteSpace(rawYear)) return string.Empty;
        string t = rawYear.Trim();
        return t.Length >= 4 && char.IsDigit(t[0]) && char.IsDigit(t[1]) && char.IsDigit(t[2]) && char.IsDigit(t[3])
            ? t[..4]
            : t;
    }

    static string ResolveCoverMimeType(byte[] bytes, string hint) {
        if (bytes != null && bytes.Length >= 4) {
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) {
                return "image/png";
            }
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) {
                return "image/jpeg";
            }
        }
        return !string.IsNullOrWhiteSpace(hint) ? hint.Trim() : "image/jpeg";
    }

    static void AppendFfMeta(StringBuilder sb, string key, string value) {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) {
            return;
        }
        sb.Append(key).Append('=');
        string v = value.Trim().Replace("\r\n", "\n").Replace('\r', '\n');
        for (int i = 0; i < v.Length; i++) {
            char c = v[i];
            if (c is '=' or ';' or '#' or '\\') {
                sb.Append('\\').Append(c);
            } else if (c == '\n') {
                sb.Append("\\\n");
            } else {
                sb.Append(c);
            }
        }
        sb.Append('\n');
    }

    static void WriteId3v23FrameHeader(Stream stream, string frameId, int size) {
        byte[] id = Encoding.ASCII.GetBytes(frameId);
        stream.Write(id, 0, 4);
        WriteBigEndianUInt32(stream, (uint)size);
        stream.WriteByte(0);
        stream.WriteByte(0);
    }

    static void WriteFlacBlockHeader(Stream stream, bool isLast, byte blockType, int length) {
        Span<byte> hdr = stackalloc byte[4];
        hdr[0] = (byte)((isLast ? 0x80 : 0x00) | (blockType & 0x7F));
        hdr[1] = (byte)((length >> 16) & 0xFF);
        hdr[2] = (byte)((length >> 8) & 0xFF);
        hdr[3] = (byte)(length & 0xFF);
        stream.Write(hdr);
    }

    static void WriteBigEndianUInt32(Stream s, uint v) {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    static void WriteLittleEndianUInt32(Stream s, uint v) {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    static void WriteLittleEndianInt32(Stream s, int v) {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    static int ReadExact(Stream s, byte[] buf, int count) {
        int total = 0;
        while (total < count) {
            int n = s.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    static void TryDeleteQuiet(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Best-effort cleanup
        }
    }
}
