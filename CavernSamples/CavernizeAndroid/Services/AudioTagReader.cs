using System.Buffers.Binary;
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
}

/// <summary>
/// Fast zero-dependency binary tag reader for MP4/M4A/M4V/MOV (moov/udta/meta/ilst),
/// FLAC (VORBIS_COMMENT), and MP3 (ID3v2) containers. Solves missing track numbers,
/// album names, and album artists in Dolby Atmos MP4/M4A files skipped by Android MediaStore.
/// </summary>
public static partial class AudioTagReader {
    [GeneratedRegex(@"^(?<disc>\d{1,2})\s*[-]\s*(?<trk>\d{1,3})(?:\s*[-._ ]\s*|\s+)(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex FilenameDiscTrackRegex();

    [GeneratedRegex(@"^(?<trk>\d{1,3})\s*(?:\.|-|_|\s)\s*(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex FilenameTrackPrefixRegex();

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
            } else {
                // Magic sniff fallback for renamed containers
                Span<byte> head = stackalloc byte[8];
                if (fs.Read(head) == 8) {
                    fs.Position = 0;
                    if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') {
                        ParseMp4Tags(fs, tags);
                    } else if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') {
                        ParseFlacTags(fs, tags);
                    } else if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') {
                        ParseId3v2Tags(fs, tags);
                    }
                }
            }
        } catch {
            // Best-effort metadata inspection
        }

        if (tags.TrackNumber <= 0) {
            ParseTrackFromFilename(IOPath.GetFileNameWithoutExtension(filePath), out int fnDisc, out int fnTrk, out _);
            if (fnTrk > 0) tags.TrackNumber = fnTrk;
            if (tags.DiscNumber <= 0 && fnDisc > 0) tags.DiscNumber = fnDisc;
        }

        return tags;
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
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extSize);
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
                    int payloadLen = (int)Math.Min(131072, Math.Max(0, subEnd - fs.Position));
                    if (payloadLen > 0) {
                        byte[] buf = new byte[payloadLen];
                        int read = fs.Read(buf, 0, payloadLen);
                        ApplyMp4Atom(atomName, typeInd, buf.AsSpan(0, read), tags);
                    }
                }
                fs.Position = subEnd;
            }
            fs.Position = itemEnd;
        }
    }

    static void ApplyMp4Atom(string atom, uint dataType, ReadOnlySpan<byte> data, ParsedAudioTags tags) {
        if (atom == "trkn" && data.Length >= 4) {
            ushort trk = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
            if (trk > 0) tags.TrackNumber = trk;
            if (data.Length >= 6) {
                ushort tot = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
                if (tot > 0) tags.TrackTotal = tot;
            }
            return;
        }
        if (atom == "disk" && data.Length >= 4) {
            ushort dsk = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
            if (dsk > 0) tags.DiscNumber = dsk;
            return;
        }
        if (dataType is not (1 or 2 or 0)) {
            return;
        }

        string text = (dataType == 2 ? Encoding.BigEndianUnicode.GetString(data) : Encoding.UTF8.GetString(data))
            .Trim('\0', ' ', '\t', '\r', '\n');
        if (string.IsNullOrEmpty(text)) return;

        switch (atom) {
            case "\u00A9nam":
                if (string.IsNullOrEmpty(tags.Title)) tags.Title = text;
                break;
            case "\u00A9ART":
                if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = text;
                break;
            case "aART":
                if (string.IsNullOrEmpty(tags.AlbumArtist)) tags.AlbumArtist = text;
                break;
            case "\u00A9alb":
                if (string.IsNullOrEmpty(tags.Album)) tags.Album = text;
                break;
            case "\u00A9trk":
                if (tags.TrackNumber <= 0 && TryParseSlashInt(text, out int t, out int tot)) {
                    tags.TrackNumber = t;
                    if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                }
                break;
            case "\u00A9lyr":
                if (string.IsNullOrEmpty(tags.Lyrics)) tags.Lyrics = text;
                break;
        }
    }

    static void ParseFlacTags(FileStream fs, ParsedAudioTags tags) {
        Span<byte> hdr = stackalloc byte[4];
        if (fs.Read(hdr) < 4 || hdr[0] != 'f' || hdr[1] != 'L' || hdr[2] != 'a' || hdr[3] != 'C') {
            return;
        }

        while (fs.Position + 4 <= fs.Length) {
            if (fs.Read(hdr) < 4) break;
            bool isLast = (hdr[0] & 0x80) != 0;
            int blockType = hdr[0] & 0x7F;
            int blockLen = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (blockLen < 0 || fs.Position + blockLen > fs.Length) break;

            if (blockType == 4 && blockLen <= 2 * 1024 * 1024) { // VORBIS_COMMENT
                byte[] buf = new byte[blockLen];
                if (fs.Read(buf, 0, blockLen) == blockLen) {
                    ParseVorbisComment(buf, tags);
                }
                break;
            }
            fs.Position += blockLen;
            if (isLast) break;
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
            if (string.IsNullOrEmpty(val)) continue;

            switch (key) {
                case "TITLE":
                    if (string.IsNullOrEmpty(tags.Title)) tags.Title = val;
                    break;
                case "ARTIST":
                    if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = val;
                    break;
                case "ALBUMARTIST":
                case "ALBUM ARTIST":
                    if (string.IsNullOrEmpty(tags.AlbumArtist)) tags.AlbumArtist = val;
                    break;
                case "ALBUM":
                    if (string.IsNullOrEmpty(tags.Album)) tags.Album = val;
                    break;
                case "TRACKNUMBER":
                case "TRACK":
                    if (tags.TrackNumber <= 0 && TryParseSlashInt(val, out int t, out int tot)) {
                        tags.TrackNumber = t;
                        if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                    }
                    break;
                case "TRACKTOTAL":
                case "TOTALTRACKS":
                    if (tags.TrackTotal <= 0 && int.TryParse(val, out int tt) && tt > 0) tags.TrackTotal = tt;
                    break;
                case "DISCNUMBER":
                case "DISC":
                    if (tags.DiscNumber <= 0 && TryParseSlashInt(val, out int d, out _)) tags.DiscNumber = d;
                    break;
                case "LYRICS":
                case "UNSYNCEDLYRICS":
                    if (string.IsNullOrEmpty(tags.Lyrics)) tags.Lyrics = val;
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
        int readLen = Math.Min(tagSize, 512 * 1024);
        if (readLen <= 0) return;

        byte[] buf = new byte[readLen];
        int got = fs.Read(buf, 0, readLen);
        int off = 0;

        if ((hdr[5] & 0x40) != 0 && got >= 4) {
            int extSize = ver == 4
                ? (((buf[0] & 0x7F) << 21) | ((buf[1] & 0x7F) << 14) | ((buf[2] & 0x7F) << 7) | (buf[3] & 0x7F))
                : BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(0, 4)) + 4;
            if (extSize > 0 && extSize < got) off = extSize;
        }

        int frameHdrLen = ver == 2 ? 6 : 10;
        while (off + frameHdrLen <= got) {
            if (buf[off] == 0) break;
            string id = ver == 2
                ? Encoding.Latin1.GetString(buf, off, 3)
                : Encoding.Latin1.GetString(buf, off, 4);
            int fSize = ver switch {
                2 => (buf[off + 3] << 16) | (buf[off + 4] << 8) | buf[off + 5],
                4 => ((buf[off + 4] & 0x7F) << 21) | ((buf[off + 5] & 0x7F) << 14) | ((buf[off + 6] & 0x7F) << 7) | (buf[off + 7] & 0x7F),
                _ => BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(off + 4, 4))
            };
            off += frameHdrLen;
            if (fSize <= 0 || off + fSize > got) break;

            if (id is "TIT2" or "TT2" or "TPE1" or "TP1" or "TPE2" or "TP2" or "TALB" or "TAL" or "TRCK" or "TRK" or "TPOS" or "TPA") {
                string txt = DecodeId3Text(buf.AsSpan(off, fSize));
                if (!string.IsNullOrEmpty(txt)) {
                    switch (id) {
                        case "TIT2":
                        case "TT2":
                            if (string.IsNullOrEmpty(tags.Title)) tags.Title = txt;
                            break;
                        case "TPE1":
                        case "TP1":
                            if (string.IsNullOrEmpty(tags.Artist)) tags.Artist = txt;
                            break;
                        case "TPE2":
                        case "TP2":
                            if (string.IsNullOrEmpty(tags.AlbumArtist)) tags.AlbumArtist = txt;
                            break;
                        case "TALB":
                        case "TAL":
                            if (string.IsNullOrEmpty(tags.Album)) tags.Album = txt;
                            break;
                        case "TRCK":
                        case "TRK":
                            if (tags.TrackNumber <= 0 && TryParseSlashInt(txt, out int t, out int tot)) {
                                tags.TrackNumber = t;
                                if (tot > 0 && tags.TrackTotal <= 0) tags.TrackTotal = tot;
                            }
                            break;
                        case "TPOS":
                        case "TPA":
                            if (tags.DiscNumber <= 0 && TryParseSlashInt(txt, out int d, out _)) tags.DiscNumber = d;
                            break;
                    }
                }
            }
            off += fSize;
        }
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
