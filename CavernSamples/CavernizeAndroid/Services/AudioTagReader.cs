using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class ParsedAudioTags {
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public int TrackNumber { get; set; }
    public int TrackTotal { get; set; }
    public int DiscNumber { get; set; }
    public string Lyrics { get; set; } = "";
    /// <summary>
    /// True only when real embedded music metadata (title, artist, album, track/disc, lyrics, or artwork)
    /// was parsed from the media container itself; never set by filename heuristics.
    /// </summary>
    public bool HasEmbeddedMusicTags { get; set; }
}

/// <summary>
/// Fast zero-dependency binary tag reader for MP4/M4A/M4V/MOV (moov/udta/meta/ilst),
/// FLAC (VORBIS_COMMENT/PICTURE), MP3 (ID3v2/ID3v1), and WAV/RIFF (LIST/INFO, id3) containers.
/// Solves missing track numbers, album names, and album artists in Dolby Atmos MP4/M4A files skipped by Android MediaStore.
/// </summary>
public static partial class AudioTagReader {
    [GeneratedRegex(@"^(?<disc>\d{1,2})\s*[-]\s*(?<trk>\d{1,3})(?:\s*[-._ ]\s*|\s+)(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex FilenameDiscTrackRegex();

    [GeneratedRegex(@"^(?<trk>\d{1,3})\s*(?:\.|-|_|\s)\s*(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex FilenameTrackPrefixRegex();

    /// <summary>
    /// Returns true if <paramref name="extensionWithDot"/> uses a container natively parsed by <see cref="AudioTagReader"/>.
    /// </summary>
    public static bool IsContainerSupported(string extensionWithDot) {
        if (string.IsNullOrEmpty(extensionWithDot)) return false;
        string ext = extensionWithDot.StartsWith('.') ? extensionWithDot.ToLowerInvariant() : "." + extensionWithDot.ToLowerInvariant();
        return ext is ".mp4" or ".m4a" or ".m4v" or ".mov" or ".qt" or ".flac" or ".mp3" or ".wav";
    }

    public static ParsedAudioTags TryReadTags(string filePath) {
        if (string.IsNullOrEmpty(filePath) || !IOFile.Exists(filePath)) {
            return null;
        }

        ParsedAudioTags tags = new();
        try {
            string ext = IOPath.GetExtension(filePath).ToLowerInvariant();
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384);
            if (fs.Length < 16) {
                return null;
            }

            if (ext is ".mp4" or ".m4a" or ".m4v" or ".mov" or ".qt") {
                ParseMp4Tags(fs, tags);
            } else if (ext == ".flac") {
                ParseFlacTags(fs, tags);
            } else if (ext == ".mp3") {
                ParseId3v2Tags(fs, tags);
                if (!tags.HasEmbeddedMusicTags) {
                    ParseId3v1Fallback(fs, tags);
                }
            } else if (ext == ".wav") {
                ParseRiffTags(fs, tags);
            } else {
                // Magic sniff fallback for renamed containers
                Span<byte> head = stackalloc byte[12];
                if (fs.Read(head) >= 8) {
                    fs.Position = 0;
                    if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') {
                        ParseMp4Tags(fs, tags);
                    } else if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') {
                        ParseFlacTags(fs, tags);
                    } else if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') {
                        ParseId3v2Tags(fs, tags);
                    } else if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
                               head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E') {
                        ParseRiffTags(fs, tags);
                    }
                }
            }
        } catch {
            // Best-effort metadata inspection
        }

        ApplyFilenameFallbacks(filePath, tags);
        return tags;
    }

    /// <summary>
    /// Populates missing track/disc numbers from the filename without altering <see cref="ParsedAudioTags.HasEmbeddedMusicTags"/>.
    /// </summary>
    static void ApplyFilenameFallbacks(string filePath, ParsedAudioTags tags) {
        if (tags == null || tags.TrackNumber > 0) return;
        ParseTrackFromFilename(IOPath.GetFileNameWithoutExtension(filePath), out int fnDisc, out int fnTrk, out _);
        if (fnTrk > 0) tags.TrackNumber = fnTrk;
        if (tags.DiscNumber <= 0 && fnDisc > 0) tags.DiscNumber = fnDisc;
    }

    public static void ParseTrackFromFilename(string fileNameWithoutExt, out int discNumber, out int trackNumber, out string strippedTitle) {
        discNumber = 0;
        trackNumber = 0;
        strippedTitle = fileNameWithoutExt ?? "";
        if (string.IsNullOrWhiteSpace(fileNameWithoutExt)) return;

        // Avoid parsing dates/timestamps like 2024-03-09 or 10-18-25 or 20240309_... as track numbers
        if (Regex.IsMatch(fileNameWithoutExt, @"^\d{4}[-_]\d{2}[-_]\d{2}") ||
            Regex.IsMatch(fileNameWithoutExt, @"^\d{2}-\d{2}-\d{2}[,_ ]") ||
            Regex.IsMatch(fileNameWithoutExt, @"^\d{7,}")) {
            return;
        }

        Match dm = FilenameDiscTrackRegex().Match(fileNameWithoutExt);
        if (dm.Success &&
            int.TryParse(dm.Groups["disc"].Value, out int dVal) && dVal is >= 1 and <= 20 &&
            int.TryParse(dm.Groups["trk"].Value, out int tVal) && tVal is >= 1 and <= 250) {
            discNumber = dVal;
            trackNumber = tVal;
            strippedTitle = dm.Groups["rest"].Value.Trim();
            return;
        }

        Match tm = FilenameTrackPrefixRegex().Match(fileNameWithoutExt);
        if (tm.Success && int.TryParse(tm.Groups["trk"].Value, out int tVal2) && tVal2 is >= 1 and <= 250) {
            trackNumber = tVal2;
            strippedTitle = tm.Groups["rest"].Value.Trim();
        }
    }

    static void ParseMp4Tags(FileStream fs, ParsedAudioTags tags) {
        WalkMp4Box(fs, fs.Length, 0, tags);
    }

    static void WalkMp4Box(FileStream fs, long endPos, int depth, ParsedAudioTags tags) {
        if (depth > 8) return;
        Span<byte> hdr = stackalloc byte[8];
        Span<byte> extSize = stackalloc byte[8];
        while (fs.Position + 8 <= endPos) {
            long boxStart = fs.Position;
            if (fs.Read(hdr) < 8) break;
            uint sz32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            long boxSize = sz32;
            if (sz32 == 1) {
                if (fs.Read(extSize) < 8) break;
                ulong extVal = BinaryPrimitives.ReadUInt64BigEndian(extSize);
                if (extVal < 16 || extVal > (ulong)long.MaxValue) break;
                boxSize = (long)extVal;
            } else if (sz32 == 0) {
                boxSize = endPos - boxStart;
            }
            if (boxSize < 8) break;
            long boxEnd = Math.Min(endPos, boxStart + boxSize);
            string boxType = Encoding.Latin1.GetString(hdr[4..8]);

            if (boxType is "moov" or "udta" or "trak" or "mdia") {
                WalkMp4Box(fs, boxEnd, depth + 1, tags);
            } else if (boxType == "meta") {
                if (fs.Position + 4 <= boxEnd) {
                    fs.Position += 4; // FullBox version + flags
                    WalkMp4Box(fs, boxEnd, depth + 1, tags);
                }
            } else if (boxType == "ilst") {
                ParseMp4Ilst(fs, boxEnd, tags);
            }

            fs.Position = boxEnd;
        }
    }

    static void ParseMp4Ilst(FileStream fs, long ilstEnd, ParsedAudioTags tags) {
        Span<byte> hdr = stackalloc byte[8];
        Span<byte> dataHdr = stackalloc byte[16];

        while (fs.Position + 8 <= ilstEnd) {
            long itemStart = fs.Position;
            if (fs.Read(hdr) < 8) break;
            uint itemSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            if (itemSize < 8) break;
            long itemEnd = Math.Min(ilstEnd, itemStart + itemSize);
            string atomName = Encoding.Latin1.GetString(hdr[4..8]);

            while (fs.Position + 8 <= itemEnd) {
                long subStart = fs.Position;
                if (fs.Read(hdr) < 8) break;
                uint subSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                if (subSize < 8) break;
                long subEnd = Math.Min(itemEnd, subStart + subSize);
                string subType = Encoding.Latin1.GetString(hdr[4..8]);

                if (subType == "data" && subEnd - fs.Position >= 8) {
                    if (fs.Read(dataHdr[..8]) < 8) break;
                    uint typeInd = BinaryPrimitives.ReadUInt32BigEndian(dataHdr[..4]) & 0xFFFFFF;
                    if (atomName == "covr") {
                        if (subEnd - fs.Position > 0 && typeInd is 13 or 14 or 27 or 0) {
                            tags.HasEmbeddedMusicTags = true;
                        }
                    } else {
                        bool isKnownBinary = atomName is "trkn" or "disk";
                        if (isKnownBinary || typeInd is 1 or 2 or 0) {
                            int payloadLen = (int)Math.Min(2 * 1024 * 1024, Math.Max(0, subEnd - fs.Position));
                            if (payloadLen > 0) {
                                byte[] buf = new byte[payloadLen];
                                int read = ReadExact(fs, buf, payloadLen);
                                ApplyMp4Atom(atomName, typeInd, buf.AsSpan(0, read), tags);
                            }
                        }
                    }
                }
                fs.Position = subEnd;
            }
            fs.Position = itemEnd;
        }
    }

    static bool IsMeaningfulTagText(string value) =>
        !string.IsNullOrWhiteSpace(value) && !"<unknown>".Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);

    static void ApplyMp4Atom(string atom, uint dataType, ReadOnlySpan<byte> data, ParsedAudioTags tags) {
        if (atom == "trkn" && data.Length >= 4) {
            ushort trk = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
            if (trk > 0) {
                tags.TrackNumber = trk;
                tags.HasEmbeddedMusicTags = true;
            }
            if (data.Length >= 6) {
                ushort tot = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
                if (tot > 0) tags.TrackTotal = tot;
            }
            return;
        }
        if (atom == "disk" && data.Length >= 4) {
            ushort dsk = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
            if (dsk > 0) {
                tags.DiscNumber = dsk;
                tags.HasEmbeddedMusicTags = true;
            }
            return;
        }
        if (dataType is not (1 or 2 or 0)) {
            return;
        }

        string text = (dataType == 2 ? Encoding.BigEndianUnicode.GetString(data) : Encoding.UTF8.GetString(data))
            .Trim('\0', ' ', '\t', '\r', '\n');
        if (!IsMeaningfulTagText(text)) return;

        switch (atom) {
            case "\u00A9nam":
                if (string.IsNullOrEmpty(tags.Title)) tags.Title = text;
                tags.HasEmbeddedMusicTags = true;
                break;
            case "\u00A9ART":
                if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = text;
                tags.HasEmbeddedMusicTags = true;
                break;
            case "aART":
                if (string.IsNullOrEmpty(tags.AlbumArtist)) tags.AlbumArtist = text;
                tags.HasEmbeddedMusicTags = true;
                break;
            case "\u00A9alb":
                if (string.IsNullOrEmpty(tags.Album)) tags.Album = text;
                tags.HasEmbeddedMusicTags = true;
                break;
            case "\u00A9trk":
                if (TryParseSlashInt(text, out int t, out int tot)) {
                    if (tags.TrackNumber <= 0) tags.TrackNumber = t;
                    if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                    tags.HasEmbeddedMusicTags = true;
                }
                break;
            case "\u00A9lyr":
                if (string.IsNullOrEmpty(tags.Lyrics)) tags.Lyrics = text;
                tags.HasEmbeddedMusicTags = true;
                break;
        }
    }

    static void ParseFlacTags(FileStream fs, ParsedAudioTags tags) {
        Span<byte> hdr = stackalloc byte[4];
        if (fs.Read(hdr) < 4 || hdr[0] != 'f' || hdr[1] != 'L' || hdr[2] != 'a' || hdr[3] != 'C') {
            return;
        }

        bool sawVorbis = false;
        bool sawPicture = false;
        while (fs.Position + 4 <= fs.Length) {
            if (fs.Read(hdr) < 4) break;
            bool isLast = (hdr[0] & 0x80) != 0;
            int blockType = hdr[0] & 0x7F;
            int blockLen = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (blockLen < 0 || fs.Position + blockLen > fs.Length) break;

            if (blockType == 4 && !sawVorbis && blockLen <= 2 * 1024 * 1024) { // VORBIS_COMMENT
                byte[] buf = new byte[blockLen];
                if (fs.Read(buf, 0, blockLen) == blockLen) {
                    ParseVorbisComment(buf, tags);
                }
                sawVorbis = true;
            } else {
                if (blockType == 6 && blockLen > 16) { // PICTURE
                    tags.HasEmbeddedMusicTags = true;
                    sawPicture = true;
                }
                fs.Position += blockLen;
            }
            if (isLast || (sawVorbis && sawPicture)) break;
        }
    }

    static void ParseVorbisComment(byte[] buf, ParsedAudioTags tags) {
        if (buf.Length < 8) return;
        int off = 0;
        int vendorLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
        off += 4;
        if (vendorLen < 0 || off + vendorLen + 4 > buf.Length) return;
        off += vendorLen;

        int count = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
        off += 4;
        bool hasSyncedLyrics = false;
        for (int i = 0; i < count && off + 4 <= buf.Length; i++) {
            int len = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            off += 4;
            if (len < 0 || off + len > buf.Length) break;
            string entry = Encoding.UTF8.GetString(buf, off, len);
            off += len;

            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            string key = entry[..eq].Trim().ToUpperInvariant();
            string val = entry[(eq + 1)..].Trim();
            if (!IsMeaningfulTagText(val)) continue;

            switch (key) {
                case "TITLE":
                    if (string.IsNullOrEmpty(tags.Title)) tags.Title = val;
                    tags.HasEmbeddedMusicTags = true;
                    break;
                case "ARTIST":
                    if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = val;
                    tags.HasEmbeddedMusicTags = true;
                    break;
                case "ALBUMARTIST":
                case "ALBUM ARTIST":
                    if (string.IsNullOrEmpty(tags.AlbumArtist)) tags.AlbumArtist = val;
                    tags.HasEmbeddedMusicTags = true;
                    break;
                case "ALBUM":
                    if (string.IsNullOrEmpty(tags.Album)) tags.Album = val;
                    tags.HasEmbeddedMusicTags = true;
                    break;
                case "TRACKNUMBER":
                case "TRACK":
                    if (TryParseSlashInt(val, out int t, out int tot)) {
                        if (tags.TrackNumber <= 0) tags.TrackNumber = t;
                        if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                        tags.HasEmbeddedMusicTags = true;
                    }
                    break;
                case "TRACKTOTAL":
                case "TOTALTRACKS":
                    if (int.TryParse(val, out int tt) && tt > 0) {
                        if (tags.TrackTotal <= 0) tags.TrackTotal = tt;
                        tags.HasEmbeddedMusicTags = true;
                    }
                    break;
                case "DISCNUMBER":
                case "DISC":
                    if (TryParseSlashInt(val, out int d, out _)) {
                        if (tags.DiscNumber <= 0) tags.DiscNumber = d;
                        tags.HasEmbeddedMusicTags = true;
                    }
                    break;
                case "SYNCEDLYRICS":
                    tags.Lyrics = val;
                    hasSyncedLyrics = true;
                    tags.HasEmbeddedMusicTags = true;
                    break;
                case "LYRICS":
                case "UNSYNCEDLYRICS":
                    if (!hasSyncedLyrics && string.IsNullOrEmpty(tags.Lyrics)) tags.Lyrics = val;
                    tags.HasEmbeddedMusicTags = true;
                    break;
            }
        }
    }

    static void ParseId3v2Tags(FileStream fs, ParsedAudioTags tags) {
        Span<byte> hdr = stackalloc byte[10];
        if (fs.Read(hdr) < 10 || hdr[0] != 'I' || hdr[1] != 'D' || hdr[2] != '3') {
            return;
        }
        int ver = hdr[3];
        if (ver < 2 || ver > 4) return;
        int tagSize = ((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F);
        if (tagSize <= 0) return;
        long tagEnd = Math.Min(fs.Length, fs.Position + tagSize);

        if ((hdr[5] & 0x40) != 0 && ver >= 3 && fs.Position + 4 <= tagEnd) {
            Span<byte> extHdr = stackalloc byte[4];
            if (fs.Read(extHdr) < 4) return;
            int extSize = ver == 4
                ? (((extHdr[0] & 0x7F) << 21) | ((extHdr[1] & 0x7F) << 14) | ((extHdr[2] & 0x7F) << 7) | (extHdr[3] & 0x7F))
                : BinaryPrimitives.ReadInt32BigEndian(extHdr) + 4;
            if (extSize > 4 && fs.Position + (extSize - 4) <= tagEnd) {
                fs.Position += extSize - 4;
            }
        }

        int frameHdrLen = ver == 2 ? 6 : 10;
        Span<byte> fHdr = stackalloc byte[10];
        bool hasSyncedLyrics = false;

        while (fs.Position + frameHdrLen <= tagEnd) {
            if (fs.Read(fHdr[..frameHdrLen]) < frameHdrLen) break;
            if (fHdr[0] == 0) break;

            string id = ver == 2
                ? Encoding.Latin1.GetString(fHdr[..3])
                : Encoding.Latin1.GetString(fHdr[..4]);
            int fSize = ver switch {
                2 => (fHdr[3] << 16) | (fHdr[4] << 8) | fHdr[5],
                4 => ((fHdr[4] & 0x7F) << 21) | ((fHdr[5] & 0x7F) << 14) | ((fHdr[6] & 0x7F) << 7) | (fHdr[7] & 0x7F),
                _ => BinaryPrimitives.ReadInt32BigEndian(fHdr.Slice(4, 4))
            };
            if (fSize <= 0 || fs.Position + fSize > tagEnd) break;
            long frameEnd = fs.Position + fSize;

            bool wantedText = id is "TIT2" or "TT2" or "TPE1" or "TP1" or "TPE2" or "TP2" or "TALB" or "TAL" or "TRCK" or "TRK" or "TPOS" or "TPA";
            bool wantedLyrics = id is "USLT" or "ULT" or "SYLT";
            if (id is "APIC" or "PIC" && fSize > 16) {
                tags.HasEmbeddedMusicTags = true;
            } else if ((wantedText || wantedLyrics) && fSize <= 2 * 1024 * 1024) {
                byte[] buf = new byte[fSize];
                int got = ReadExact(fs, buf, fSize);
                if (got > 0) {
                    ReadOnlySpan<byte> payload = buf.AsSpan(0, got);
                    if (wantedText) {
                        string txt = DecodeId3Text(payload);
                        if (IsMeaningfulTagText(txt)) {
                            switch (id) {
                                case "TIT2":
                                case "TT2":
                                    if (string.IsNullOrEmpty(tags.Title)) tags.Title = txt;
                                    tags.HasEmbeddedMusicTags = true;
                                    break;
                                case "TPE1":
                                case "TP1":
                                    if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = txt;
                                    tags.HasEmbeddedMusicTags = true;
                                    break;
                                case "TPE2":
                                case "TP2":
                                    if (string.IsNullOrEmpty(tags.AlbumArtist)) tags.AlbumArtist = txt;
                                    tags.HasEmbeddedMusicTags = true;
                                    break;
                                case "TALB":
                                case "TAL":
                                    if (string.IsNullOrEmpty(tags.Album)) tags.Album = txt;
                                    tags.HasEmbeddedMusicTags = true;
                                    break;
                                case "TRCK":
                                case "TRK":
                                    if (TryParseSlashInt(txt, out int t, out int tot)) {
                                        if (tags.TrackNumber <= 0) tags.TrackNumber = t;
                                        if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                                        tags.HasEmbeddedMusicTags = true;
                                    }
                                    break;
                                case "TPOS":
                                case "TPA":
                                    if (TryParseSlashInt(txt, out int d, out _)) {
                                        if (tags.DiscNumber <= 0) tags.DiscNumber = d;
                                        tags.HasEmbeddedMusicTags = true;
                                    }
                                    break;
                            }
                        }
                    } else if (id == "SYLT") {
                        string syltLrc = ParseId3SyltAsLrc(payload);
                        if (!string.IsNullOrEmpty(syltLrc)) {
                            tags.Lyrics = syltLrc;
                            hasSyncedLyrics = true;
                            tags.HasEmbeddedMusicTags = true;
                        }
                    } else if (!hasSyncedLyrics && string.IsNullOrEmpty(tags.Lyrics)) {
                        string uslt = ParseId3Uslt(payload);
                        if (IsMeaningfulTagText(uslt)) {
                            tags.Lyrics = uslt;
                            tags.HasEmbeddedMusicTags = true;
                        }
                    }
                }
            }

            // Seek cleanly past APIC/artwork and any large or unhandled frames without capping the tag scan
            fs.Position = frameEnd;
        }
    }

    static void ParseId3v1Fallback(FileStream fs, ParsedAudioTags tags) {
        if (fs.Length < 128) return;
        fs.Position = fs.Length - 128;
        byte[] buf = new byte[128];
        if (ReadExact(fs, buf, 128) < 128) return;
        if (buf[0] != 'T' || buf[1] != 'A' || buf[2] != 'G') return;

        string title = Encoding.Latin1.GetString(buf, 3, 30).TrimEnd('\0', ' ');
        string artist = Encoding.Latin1.GetString(buf, 33, 30).TrimEnd('\0', ' ');
        string album = Encoding.Latin1.GetString(buf, 63, 30).TrimEnd('\0', ' ');
        int trk = (buf[125] == 0 && buf[126] > 0) ? buf[126] : 0;

        if (IsMeaningfulTagText(title)) {
            if (string.IsNullOrEmpty(tags.Title)) tags.Title = title;
            tags.HasEmbeddedMusicTags = true;
        }
        if (IsMeaningfulTagText(artist)) {
            if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = artist;
            tags.HasEmbeddedMusicTags = true;
        }
        if (IsMeaningfulTagText(album)) {
            if (string.IsNullOrEmpty(tags.Album)) tags.Album = album;
            tags.HasEmbeddedMusicTags = true;
        }
        if (trk > 0) {
            if (tags.TrackNumber <= 0) tags.TrackNumber = trk;
            tags.HasEmbeddedMusicTags = true;
        }
    }

    static void ParseRiffTags(FileStream fs, ParsedAudioTags tags) {
        Span<byte> hdr = stackalloc byte[12];
        if (fs.Read(hdr) < 12) return;
        if (hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F' ||
            hdr[8] != 'W' || hdr[9] != 'A' || hdr[10] != 'V' || hdr[11] != 'E') {
            return;
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
        long endPos = Math.Min(fs.Length, 8L + riffSize);
        Span<byte> chunkHdr = stackalloc byte[8];
        Span<byte> listType = stackalloc byte[4];

        for (int n = 0; n < 256 && fs.Position + 8 <= endPos; n++) {
            if (fs.Read(chunkHdr) < 8) break;
            string chunkId = Encoding.Latin1.GetString(chunkHdr[..4]);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr.Slice(4, 4));
            long payloadStart = fs.Position;
            long nextPos = payloadStart + chunkSize + (chunkSize & 1L);

            if (chunkId == "LIST" && chunkSize >= 4 && payloadStart + 4 <= endPos) {
                if (fs.Read(listType) == 4 && listType[0] == 'I' && listType[1] == 'N' && listType[2] == 'F' && listType[3] == 'O') {
                    int infoLen = (int)Math.Min(262144L, Math.Min(endPos - fs.Position, (long)chunkSize - 4L));
                    if (infoLen >= 8) {
                        byte[] buf = new byte[infoLen];
                        int got = ReadExact(fs, buf, infoLen);
                        ParseRiffInfoSubchunks(buf.AsSpan(0, got), tags);
                    }
                }
            } else if (chunkId is "id3 " or "ID3 " && chunkSize >= 10 && payloadStart + 10 <= endPos) {
                ParseId3v2Tags(fs, tags);
            }

            if (nextPos <= payloadStart) break;
            fs.Position = Math.Min(endPos, nextPos);
        }
    }

    static void ParseRiffInfoSubchunks(ReadOnlySpan<byte> span, ParsedAudioTags tags) {
        int off = 0;
        while (off + 8 <= span.Length) {
            string id = Encoding.Latin1.GetString(span.Slice(off, 4));
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 4, 4));
            off += 8;
            if (sz > (uint)(span.Length - off)) break;

            string text = Encoding.UTF8.GetString(span.Slice(off, (int)sz)).Trim('\0', ' ', '\t', '\r', '\n');
            if (IsMeaningfulTagText(text)) {
                switch (id) {
                    case "INAM":
                        if (string.IsNullOrEmpty(tags.Title)) tags.Title = text;
                        tags.HasEmbeddedMusicTags = true;
                        break;
                    case "IART":
                        if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = text;
                        tags.HasEmbeddedMusicTags = true;
                        break;
                    case "IPRD":
                        if (string.IsNullOrEmpty(tags.Album)) tags.Album = text;
                        tags.HasEmbeddedMusicTags = true;
                        break;
                    case "ITRK":
                        if (TryParseSlashInt(text, out int t, out int tot)) {
                            if (tags.TrackNumber <= 0) tags.TrackNumber = t;
                            if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                            tags.HasEmbeddedMusicTags = true;
                        }
                        break;
                }
            }

            int step = (int)sz + ((int)sz & 1);
            if (step <= 0 && sz == 0) continue;
            off += step;
        }
    }

    static int ReadExact(FileStream fs, byte[] buf, int count) {
        int total = 0;
        while (total < count) {
            int n = fs.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    static string ParseId3Uslt(ReadOnlySpan<byte> frame) {
        if (frame.Length < 5) return "";
        byte enc = frame[0];
        int i = 4; // encoding (1) + lang (3)
        SkipNullTerminatedString(frame, enc, ref i);
        if (i >= frame.Length) return "";
        return DecodeId3String(frame[i..], enc).TrimEnd('\0');
    }

    static string ParseId3SyltAsLrc(ReadOnlySpan<byte> frame) {
        if (frame.Length < 7) return "";
        byte enc = frame[0];
        byte tsFormat = frame[4]; // 2 = milliseconds
        int i = 6; // enc (1) + lang (3) + tsFormat (1) + contentType (1)
        SkipNullTerminatedString(frame, enc, ref i);

        List<(double sec, string text)> cues = [];
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
                cues.Add((sec, txt));
            }
            i = tsPos + 4;
        }
        if (cues.Count < 2) return "";
        cues.Sort((a, b) => a.sec.CompareTo(b.sec));
        StringBuilder sb = new(cues.Count * 32);
        foreach (var (sec, text) in cues) {
            int mins = (int)(sec / 60.0);
            double rem = Math.Max(0.0, sec - mins * 60.0);
            sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:00}:{1:00.000}] {2}\n", mins, rem, text);
        }
        return sb.ToString();
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
        return decoder.GetString(bytes);
    }

    static string DecodeId3Text(ReadOnlySpan<byte> payload) {
        if (payload.Length < 2) return "";
        byte enc = payload[0];
        ReadOnlySpan<byte> body = payload[1..];
        string raw = enc switch {
            0 => Encoding.Latin1.GetString(body),
            1 => DecodeUtf16WithBom(body),
            2 => Encoding.BigEndianUnicode.GetString(body),
            _ => Encoding.UTF8.GetString(body)
        };
        return raw.Trim('\0', ' ', '\t', '\r', '\n');
    }

    static string DecodeUtf16WithBom(ReadOnlySpan<byte> data) {
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) {
            return Encoding.BigEndianUnicode.GetString(data[2..]);
        }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) {
            return Encoding.Unicode.GetString(data[2..]);
        }
        return Encoding.Unicode.GetString(data);
    }

    static bool TryParseSlashInt(string text, out int primary, out int total) {
        primary = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split('/');
        if (!int.TryParse(parts[0].Trim(), out primary) || primary <= 0) {
            primary = 0;
            return false;
        }
        if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int tot) && tot > 0) {
            total = tot;
        }
        return true;
    }
}
