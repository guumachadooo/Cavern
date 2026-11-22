using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace OmniAgentAndroid.Services;

/// <summary>
/// Zero-Trust v4 E2E cryptography matching <c>.agents/agent/omni_protocol.py</c>
/// and <c>.agents/docs/cloudflare_live_relay_worker.js</c> bit-for-bit.
/// </summary>
public static class OmniCrypto {
    public static string NormalizePairSecretHex(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return "";
        }
        string s = raw.Trim();
        foreach (string prefix in new[] { "CAVPAIR-", "CAVPAIR_", "CAVPAIR:", "OMNIPAIR-", "PAIR-" }) {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                s = s[prefix.Length..];
                break;
            }
        }
        System.Text.StringBuilder sb = new(64);
        foreach (char c in s) {
            if (Uri.IsHexDigit(c)) {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.Length == 64 ? sb.ToString() : "";
    }

    public static string GeneratePairSecretHex() {
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string FormatPairKeyDisplay(string hex64) {
        if (string.IsNullOrEmpty(hex64) || hex64.Length != 64) {
            return "";
        }
        return $"CAVPAIR-{hex64[..8].ToUpperInvariant()}-...-{hex64[^8..].ToUpperInvariant()}";
    }

    public static string ComputeDeviceHash(string pairHex) =>
        Sha256Hex($"wake-channel-v2:{pairHex}");

    public static string ComputeAgentKey(string pairHex) =>
        Sha256Hex($"agent-access-v4:{pairHex}");

    public static string Sha256Hex(string utf8Text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(utf8Text))).ToLowerInvariant();

    public static object EncryptEnvelope(string pairHex, string contextId, object payloadObj) {
        byte[] key = Convert.FromHexString(pairHex);
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        byte[] encKey = HMACSHA256.HashData(key, Concat(System.Text.Encoding.UTF8.GetBytes("omni-enc-v4:"), iv));
        byte[] macKey = HMACSHA256.HashData(key, Concat(System.Text.Encoding.UTF8.GetBytes("omni-mac-v4:"), iv));

        byte[] rawPlain = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj));
        byte[] gzPlain = GzipCompress(rawPlain);
        bool useGz = gzPlain.Length < rawPlain.Length;
        byte[] plain = useGz ? gzPlain : rawPlain;

        byte[] ks = CtrKeystream(encKey, iv, plain.Length);
        byte[] ct = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++) {
            ct[i] = (byte)(plain[i] ^ ks[i]);
        }

        byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"omni-v4|{contextId}|{(useGz ? "1" : "0")}|");
        byte[] macInput = Concat(prefix, iv, ct);
        byte[] tag = HMACSHA256.HashData(macKey, macInput);

        return new {
            v = 4,
            gz = useGz,
            nonce = Convert.ToBase64String(iv),
            ct = Convert.ToBase64String(ct),
            tag = Convert.ToBase64String(tag)
        };
    }

    public static JsonDocument DecryptEnvelope(string pairHex, string contextId, JsonElement envelope) {
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("v", out JsonElement vEl) ||
            vEl.GetInt32() != 4) {
            throw new InvalidOperationException("Unsupported OmniAgent envelope version.");
        }
        byte[] key = Convert.FromHexString(pairHex);
        byte[] iv = Convert.FromBase64String(envelope.GetProperty("nonce").GetString() ?? "");
        byte[] ct = Convert.FromBase64String(envelope.GetProperty("ct").GetString() ?? "");
        byte[] tag = Convert.FromBase64String(envelope.GetProperty("tag").GetString() ?? "");
        bool useGz = envelope.TryGetProperty("gz", out JsonElement gzEl) && gzEl.GetBoolean();

        byte[] macKey = HMACSHA256.HashData(key, Concat(System.Text.Encoding.UTF8.GetBytes("omni-mac-v4:"), iv));
        byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"omni-v4|{contextId}|{(useGz ? "1" : "0")}|");
        byte[] expectedTag = HMACSHA256.HashData(macKey, Concat(prefix, iv, ct));
        if (tag.Length != expectedTag.Length || !CryptographicOperations.FixedTimeEquals(tag, expectedTag)) {
            throw new CryptographicException("Envelope HMAC-SHA256 verification failed.");
        }

        byte[] encKey = HMACSHA256.HashData(key, Concat(System.Text.Encoding.UTF8.GetBytes("omni-enc-v4:"), iv));
        byte[] ks = CtrKeystream(encKey, iv, ct.Length);
        byte[] plain = new byte[ct.Length];
        for (int i = 0; i < ct.Length; i++) {
            plain[i] = (byte)(ct[i] ^ ks[i]);
        }
        if (useGz) {
            plain = GzipDecompress(plain);
        }
        return JsonDocument.Parse(plain);
    }

    static byte[] CtrKeystream(byte[] encKey, byte[] nonce, int length) {
        byte[] outBytes = new byte[length];
        byte[] blockInput = new byte[encKey.Length + nonce.Length + 4];
        Buffer.BlockCopy(encKey, 0, blockInput, 0, encKey.Length);
        Buffer.BlockCopy(nonce, 0, blockInput, encKey.Length, nonce.Length);
        int offset = 0;
        uint counter = 0;
        while (offset < length) {
            int cOff = encKey.Length + nonce.Length;
            blockInput[cOff] = (byte)(counter >> 24);
            blockInput[cOff + 1] = (byte)(counter >> 16);
            blockInput[cOff + 2] = (byte)(counter >> 8);
            blockInput[cOff + 3] = (byte)counter;
            byte[] block = SHA256.HashData(blockInput);
            int take = Math.Min(block.Length, length - offset);
            Buffer.BlockCopy(block, 0, outBytes, offset, take);
            offset += take;
            counter++;
        }
        return outBytes;
    }

    static byte[] GzipCompress(byte[] input) {
        using MemoryStream ms = new();
        using (GZipStream gz = new(ms, CompressionLevel.Optimal, leaveOpen: true)) {
            gz.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    public static byte[] GzipDecompress(byte[] input) {
        using MemoryStream src = new(input);
        using GZipStream gz = new(src, CompressionMode.Decompress);
        using MemoryStream dst = new();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    static byte[] Concat(params byte[][] parts) {
        int total = 0;
        foreach (byte[] p in parts) {
            total += p.Length;
        }
        byte[] res = new byte[total];
        int off = 0;
        foreach (byte[] p in parts) {
            Buffer.BlockCopy(p, 0, res, off, p.Length);
            off += p.Length;
        }
        return res;
    }
}
