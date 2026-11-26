using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Security.Keystore;

using CavernizeAndroid.Audio;

using Java.Security;

using Javax.Crypto;
using Javax.Crypto.Spec;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace CavernizeAndroid.Services;


public static partial class LiveDiagnosticBridgeService {
    // ==================== PAIRING SECRET & CRYPTO (ANDROID KEYSTORE + ENCRYPT-THEN-MAC) ====================

    public static bool HasPairedSecret(AppSettingsStore store) =>
        !string.IsNullOrWhiteSpace(store?.LiveBridgeEncryptedPairSecret) &&
        !string.IsNullOrEmpty(LoadPairSecretHex(store));

    public static string GenerateAndStorePairSecret(AppSettingsStore store) {
        if (store == null) {
            return "";
        }
        byte[] raw = new byte[32];
        RandomNumberGenerator.Fill(raw);
        string hex = Convert.ToHexString(raw).ToLowerInvariant();
        string encrypted = EncryptWithKeystore(hex);
        if (string.IsNullOrEmpty(encrypted)) {
            return "";
        }
        store.LiveBridgeEncryptedPairSecret = encrypted;
        AppLogger.Info("CLOUD", "Generated & stored new 256-bit Live Bridge pairing secret in Android Keystore.");
        NotifyStateChanged();
        return $"CAVPAIR-{hex}";
    }

    public static string GetCopyablePairSecret(AppSettingsStore store) {
        string hex = LoadPairSecretHex(store);
        return string.IsNullOrEmpty(hex) ? "" : $"CAVPAIR-{hex}";
    }

    public static void RevokePairSecret(AppSettingsStore store) {
        if (store == null) {
            return;
        }
        store.LiveBridgeEncryptedPairSecret = "";
        store.LiveBridgeStandbyEnabled = false;
        AppLogger.Info("CLOUD", "Revoked Live Bridge pairing secret and disabled background wake.");
        NotifyStateChanged();
    }

    internal static string LoadPairSecretHex(AppSettingsStore store) {
        string stored = store?.LiveBridgeEncryptedPairSecret;
        if (string.IsNullOrWhiteSpace(stored)) {
            return "";
        }
        string hex = DecryptWithKeystore(stored).Trim().ToLowerInvariant();
        return hex.Length == 64 ? hex : "";
    }

    internal static string GetPairSecretHexForInternalAuth(AppSettingsStore store) => LoadPairSecretHex(store);

    internal static string ComputeDeviceHash(string pairSecretHex) => ComputeWakeChannelHash(pairSecretHex);

    internal static string ComputeWakeChannelHash(string pairSecretHex) {
        byte[] input = System.Text.Encoding.UTF8.GetBytes($"wake-channel-v2:{pairSecretHex.ToLowerInvariant()}");
        byte[] digest = SHA256.HashData(input);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static string ComputeAgentKey(string pairSecretHex) {
        byte[] input = System.Text.Encoding.UTF8.GetBytes($"agent-access-v4:{pairSecretHex.ToLowerInvariant()}");
        byte[] digest = SHA256.HashData(input);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the public double-hashed Smart Knock mailbox ID (<c>sha256("knock-channel-v5:" + deviceHash)</c>).
    /// Safe to store in public <c>.agents/bridge_profile.json</c> (cannot derive deviceHash or pairSecret).
    /// </summary>
    public static string ComputeKnockChannelId(string pairSecretHex) {
        if (string.IsNullOrWhiteSpace(pairSecretHex) || pairSecretHex.Length != 64) {
            return "";
        }
        string devHash = ComputeWakeChannelHash(pairSecretHex);
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"knock-channel-v5:{devHash}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string GetPublicKnockId(AppSettingsStore store) {
        string hex = LoadPairSecretHex(store);
        return string.IsNullOrEmpty(hex) ? "" : ComputeKnockChannelId(hex);
    }

    /// <summary>
    /// Computes the 6-character Visual Verification Code (SAS, <c>XXX-XXX</c>) matching Python <c>omni_protocol.py</c>.
    /// </summary>
    public static string ComputeKnockSasCode(string knockId, string knockReqId, string clientPubKeyB64) {
        string canonical = $"knock-sas-v5|{(knockId ?? "").Trim().ToLowerInvariant()}|{(knockReqId ?? "").Trim()}|{(clientPubKeyB64 ?? "").Trim()}";
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        char[] chars = new char[6];
        for (int i = 0; i < 6; i++) {
            chars[i] = CodeAlphabet[digest[i] & 0x1F];
        }
        return $"{new string(chars, 0, 3)}-{new string(chars, 3, 3)}";
    }

    /// <summary>
    /// Performs ephemeral NIST P-256 ECDH with <paramref name="clientPubKeyB64"/> (65-byte SEC1 uncompressed)
    /// and encrypts <paramref name="grantObj"/> using v4 Encrypt-then-MAC (SHA256-CTR + HMAC-SHA256).
    /// </summary>
    internal static (string serverPubKeyB64, object encryptedGrant) CreateEcdhP256GrantEnvelope(
        string clientPubKeyB64,
        string knockReqId,
        object grantObj) {
        byte[] clientRaw = Convert.FromBase64String((clientPubKeyB64 ?? "").Trim());
        if (clientRaw.Length != 65 || clientRaw[0] != 0x04) {
            throw new CryptographicException("Invalid client P-256 SEC1 public key.");
        }
        ECParameters peerParams = new() {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint {
                X = clientRaw[1..33],
                Y = clientRaw[33..65]
            }
        };
        using ECDiffieHellman peerDh = ECDiffieHellman.Create(peerParams);
        using ECDiffieHellman localDh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters localPub = localDh.ExportParameters(includePrivateParameters: false);
        byte[] serverRaw = new byte[65];
        serverRaw[0] = 0x04;
        Buffer.BlockCopy(localPub.Q.X, 0, serverRaw, 1 + (32 - localPub.Q.X.Length), localPub.Q.X.Length);
        Buffer.BlockCopy(localPub.Q.Y, 0, serverRaw, 33 + (32 - localPub.Q.Y.Length), localPub.Q.Y.Length);
        string serverPubB64 = Convert.ToBase64String(serverRaw);

        byte[] sharedX = localDh.DeriveRawSecretAgreement(peerDh.PublicKey);
        byte[] symKeyBytes = SHA256.HashData(ConcatBytes(
            System.Text.Encoding.UTF8.GetBytes($"ecdh-knock-v5|{(knockReqId ?? "").Trim()}|"),
            sharedX));
        string symKeyHex = Convert.ToHexString(symKeyBytes).ToLowerInvariant();
        object env = EncryptV4Envelope(symKeyHex, $"knock:{(knockReqId ?? "").Trim()}", grantObj);
        return (serverPubB64, env);
    }

    internal static object EncryptV4Envelope(string keyHex64, string contextId, object payloadObj) {
        byte[] key = Convert.FromHexString(keyHex64);
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        byte[] encKey = HMACSHA256.HashData(key, ConcatBytes(System.Text.Encoding.UTF8.GetBytes("omni-enc-v4:"), iv));
        byte[] macKey = HMACSHA256.HashData(key, ConcatBytes(System.Text.Encoding.UTF8.GetBytes("omni-mac-v4:"), iv));

        byte[] rawPlain = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj));
        byte[] gzPlain;
        using (MemoryStream ms = new()) {
            using (GZipStream gz = new(ms, CompressionLevel.Optimal, leaveOpen: true)) {
                gz.Write(rawPlain, 0, rawPlain.Length);
            }
            gzPlain = ms.ToArray();
        }
        bool useGz = gzPlain.Length < rawPlain.Length;
        byte[] plain = useGz ? gzPlain : rawPlain;

        byte[] ct = new byte[plain.Length];
        uint counter = 0;
        int offset = 0;
        byte[] ctrBuf = new byte[4];
        while (offset < plain.Length) {
            ctrBuf[0] = (byte)((counter >> 24) & 0xFF);
            ctrBuf[1] = (byte)((counter >> 16) & 0xFF);
            ctrBuf[2] = (byte)((counter >> 8) & 0xFF);
            ctrBuf[3] = (byte)(counter & 0xFF);
            byte[] ksBlock = SHA256.HashData(ConcatBytes(encKey, iv, ctrBuf));
            int take = Math.Min(ksBlock.Length, plain.Length - offset);
            for (int i = 0; i < take; i++) {
                ct[offset + i] = (byte)(plain[offset + i] ^ ksBlock[i]);
            }
            offset += take;
            counter++;
        }

        byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"omni-v4|{contextId}|{(useGz ? "1" : "0")}|");
        byte[] tag = HMACSHA256.HashData(macKey, ConcatBytes(prefix, iv, ct));
        return new {
            v = 4,
            gz = useGz,
            nonce = Convert.ToBase64String(iv),
            ct = Convert.ToBase64String(ct),
            tag = Convert.ToBase64String(tag)
        };
    }

    internal static JsonDocument DecryptV4Envelope(string keyHex64, string contextId, JsonElement envelope) {
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("v", out JsonElement vEl) ||
            vEl.GetInt32() != 4) {
            throw new InvalidOperationException("Unsupported OmniAgent v4 envelope version.");
        }
        byte[] key = Convert.FromHexString(keyHex64);
        byte[] iv = Convert.FromBase64String(envelope.GetProperty("nonce").GetString() ?? "");
        byte[] ct = Convert.FromBase64String(envelope.GetProperty("ct").GetString() ?? "");
        byte[] tag = Convert.FromBase64String(envelope.GetProperty("tag").GetString() ?? "");
        bool useGz = envelope.TryGetProperty("gz", out JsonElement gzEl) && gzEl.GetBoolean();

        byte[] macKey = HMACSHA256.HashData(key, ConcatBytes(System.Text.Encoding.UTF8.GetBytes("omni-mac-v4:"), iv));
        byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"omni-v4|{contextId}|{(useGz ? "1" : "0")}|");
        byte[] expectedTag = HMACSHA256.HashData(macKey, ConcatBytes(prefix, iv, ct));
        if (tag.Length != expectedTag.Length || !CryptographicOperations.FixedTimeEquals(tag, expectedTag)) {
            throw new CryptographicException("Direct agent envelope HMAC-SHA256 verification failed.");
        }

        byte[] encKey = HMACSHA256.HashData(key, ConcatBytes(System.Text.Encoding.UTF8.GetBytes("omni-enc-v4:"), iv));
        byte[] plain = new byte[ct.Length];
        uint counter = 0;
        int offset = 0;
        byte[] ctrBuf = new byte[4];
        while (offset < ct.Length) {
            ctrBuf[0] = (byte)((counter >> 24) & 0xFF);
            ctrBuf[1] = (byte)((counter >> 16) & 0xFF);
            ctrBuf[2] = (byte)((counter >> 8) & 0xFF);
            ctrBuf[3] = (byte)(counter & 0xFF);
            byte[] ksBlock = SHA256.HashData(ConcatBytes(encKey, iv, ctrBuf));
            int take = Math.Min(ksBlock.Length, ct.Length - offset);
            for (int i = 0; i < take; i++) {
                plain[offset + i] = (byte)(ct[offset + i] ^ ksBlock[i]);
            }
            offset += take;
            counter++;
        }

        if (useGz) {
            using MemoryStream src = new(plain);
            using GZipStream gz = new(src, CompressionMode.Decompress);
            using MemoryStream dst = new();
            gz.CopyTo(dst);
            plain = dst.ToArray();
        }
        return JsonDocument.Parse(plain);
    }

    internal static bool VerifyWakeRequestSignature(string pairSecretHex, LiveBridgeWakeRequest req, out string rejectReason) {
        rejectReason = "";
        if (req == null || string.IsNullOrWhiteSpace(req.WakeId) || string.IsNullOrWhiteSpace(req.Signature)) {
            rejectReason = "Malformed wake payload.";
            return false;
        }
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowUnix - req.TimestampUnix) > 180) {
            rejectReason = "Wake timestamp expired or clock skew > 180s.";
            return false;
        }
        lock (nonceLock) {
            if (recentWakeNonces.Contains(req.WakeId)) {
                rejectReason = "Duplicate wake nonce rejected.";
                return false;
            }
        }
        try {
            byte[] key = Convert.FromHexString(pairSecretHex);
            string canonical = $"wake-v2|{req.WakeId}|{req.TimestampUnix}|{req.RequestedTier}|{req.DurationSeconds}|{req.Reason}";
            byte[] expectedBytes = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(canonical));
            byte[] providedBytes = Convert.FromHexString(req.Signature.Trim());
            if (expectedBytes.Length != providedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)) {
                rejectReason = "Invalid HMAC-SHA256 wake signature.";
                return false;
            }
            lock (nonceLock) {
                recentWakeNonces.Add(req.WakeId);
                recentWakeNoncesQueue.Enqueue(req.WakeId);
                while (recentWakeNoncesQueue.Count > 64) {
                    string oldest = recentWakeNoncesQueue.Dequeue();
                    recentWakeNonces.Remove(oldest);
                }
            }
            return true;
        } catch {
            rejectReason = "Wake signature verification error.";
            return false;
        }
    }

    /// <summary>
    /// Encrypt-then-MAC (SHA256-CTR + HMAC-SHA256) matching Python stdlib live_bridge_client.py bit-for-bit.
    /// </summary>
    internal static object EncryptApprovalEnvelope(string pairSecretHex, string wakeId, object plaintextObj) {
        byte[] key = Convert.FromHexString(pairSecretHex);
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        byte[] encPrefix = System.Text.Encoding.UTF8.GetBytes("enc-v2:");
        byte[] macPrefix = System.Text.Encoding.UTF8.GetBytes("mac-v2:");
        byte[] encKey = HMACSHA256.HashData(key, ConcatBytes(encPrefix, iv));
        byte[] macKey = HMACSHA256.HashData(key, ConcatBytes(macPrefix, iv));

        string json = JsonSerializer.Serialize(plaintextObj);
        byte[] plain = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] ct = new byte[plain.Length];

        uint counter = 0;
        int offset = 0;
        byte[] ctrBuf = new byte[4];
        while (offset < plain.Length) {
            ctrBuf[0] = (byte)((counter >> 24) & 0xFF);
            ctrBuf[1] = (byte)((counter >> 16) & 0xFF);
            ctrBuf[2] = (byte)((counter >> 8) & 0xFF);
            ctrBuf[3] = (byte)(counter & 0xFF);
            byte[] ksBlock = SHA256.HashData(ConcatBytes(encKey, iv, ctrBuf));
            int take = Math.Min(ksBlock.Length, plain.Length - offset);
            for (int i = 0; i < take; i++) {
                ct[offset + i] = (byte)(plain[offset + i] ^ ksBlock[i]);
            }
            offset += take;
            counter++;
        }

        byte[] macHeader = System.Text.Encoding.UTF8.GetBytes($"wake-approve-v2|{wakeId}|");
        byte[] tag = HMACSHA256.HashData(macKey, ConcatBytes(macHeader, iv, ct));

        return new {
            v = 2,
            nonce = Convert.ToBase64String(iv),
            ct = Convert.ToBase64String(ct),
            tag = Convert.ToBase64String(tag)
        };
    }

    static byte[] ConcatBytes(params byte[][] arrays) {
        int total = 0;
        foreach (byte[] a in arrays) {
            total += a.Length;
        }
        byte[] res = new byte[total];
        int pos = 0;
        foreach (byte[] a in arrays) {
            Buffer.BlockCopy(a, 0, res, pos, a.Length);
            pos += a.Length;
        }
        return res;
    }

    internal static void StorePendingWakeRequest(LiveBridgeWakeRequest req) {
        if (req != null && !string.IsNullOrEmpty(req.WakeId)) {
            pendingWakeRequests[req.WakeId] = req;
        }
    }

    internal static LiveBridgeWakeRequest GetPendingWakeRequest(string wakeId) =>
        !string.IsNullOrEmpty(wakeId) && pendingWakeRequests.TryGetValue(wakeId, out LiveBridgeWakeRequest r) ? r : null;

    internal static async Task<(bool ok, string message)> CompleteWakeApprovalAsync(
        Context context,
        AppSettingsStore store,
        string wakeId,
        int approvedTier,
        int durationSeconds,
        bool biometricVerified,
        CancellationToken ct = default) {
        if (context == null || store == null) {
            return (false, "Context unavailable.");
        }
        pendingWakeRequests.TryRemove(wakeId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex)) {
            return (false, "Pairing secret missing.");
        }
        if (!TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
            return (false, "Relay URL invalid.");
        }

        int finalTier = Math.Clamp(approvedTier, 0, 2);
        int finalTtl = Math.Clamp(durationSeconds, 300, 3600);
        (bool started, string sessionCode, string startMsg) = await StartSessionAsync(
            context,
            store,
            maxTier: finalTier,
            durationSeconds: finalTtl,
            biometricVerified: biometricVerified,
            ct: ct).ConfigureAwait(false);
        if (!started) {
            return (false, startMsg);
        }

        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
        object envelope = EncryptApprovalEnvelope(pairHex, wakeId, new {
            sessionCode,
            tier = finalTier,
            ttlSeconds = finalTtl,
            biometricVerified,
            device = $"{Build.Manufacturer} {Build.Model}".Trim(),
            appVersion = $"v{ver} ({build})",
            approvedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });

        string devHash = ComputeWakeChannelHash(pairHex);
        try {
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/wake/approve");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new { wakeId, encryptedSession = envelope }),
                System.Text.Encoding.UTF8,
                "application/json");
            using HttpResponseMessage resp = await http.SendAsync(post, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                return (false, $"Relay wake approval failed: HTTP {(int)resp.StatusCode}");
            }
            AppLogger.Info("CLOUD", $"Live Bridge biometric wake approved (wakeId={wakeId}, tier=T{finalTier}, ttl={finalTtl}s).");
            return (true, $"Connected (Tier {finalTier}, {finalTtl / 60} min)");
        } catch (Exception ex) {
            return (false, $"Wake approve network error: {ex.Message}");
        }
    }

    internal static async Task DenyWakeRequestAsync(AppSettingsStore store, string wakeId, string reason) {
        pendingWakeRequests.TryRemove(wakeId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex) || !TryNormalizeRelayUrl(store?.LiveRelayUrl, out string baseUrl)) {
            return;
        }
        try {
            string devHash = ComputeWakeChannelHash(pairHex);
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/wake/deny");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new { wakeId, reason = reason ?? "Denied by user" }),
                System.Text.Encoding.UTF8,
                "application/json");
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage _ = await http.SendAsync(post, cts.Token).ConfigureAwait(false);
            AppLogger.Info("CLOUD", $"Live Bridge wake request denied (wakeId={wakeId}): {reason}");
        } catch {
            // Best effort
        }
    }

    internal static void StorePendingKnockRequest(LiveBridgeKnockRequest req) {
        if (req != null && !string.IsNullOrEmpty(req.KnockReqId)) {
            pendingKnockRequests[req.KnockReqId] = req;
        }
    }

    internal static LiveBridgeKnockRequest GetPendingKnockRequest(string knockReqId) =>
        !string.IsNullOrEmpty(knockReqId) && pendingKnockRequests.TryGetValue(knockReqId, out LiveBridgeKnockRequest r) ? r : null;

    internal static async Task<(bool ok, string message)> CompleteKnockApprovalAsync(
        Context context,
        AppSettingsStore store,
        LiveBridgeKnockRequest knockReq,
        bool rememberWorkspace,
        bool biometricVerified,
        CancellationToken ct = default) {
        if (context == null || store == null || knockReq == null) {
            return (false, "Invalid Smart Knock context.");
        }
        pendingKnockRequests.TryRemove(knockReq.KnockReqId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex)) {
            return (false, "Pairing secret missing.");
        }
        if (!TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
            return (false, "Relay URL invalid.");
        }

        // Strict least-privilege invariant: workspace persistent pair grant requires genuine Biometric verification
        bool grantPair = rememberWorkspace && biometricVerified;
        int finalTier = Math.Clamp(knockReq.RequestedTier, 0, 2);
        int finalTtl = Math.Clamp(knockReq.DurationSeconds, 300, 3600);
        (bool started, string sessionCode, string startMsg) = await StartSessionAsync(
            context,
            store,
            maxTier: finalTier,
            durationSeconds: finalTtl,
            biometricVerified: biometricVerified,
            ct: ct).ConfigureAwait(false);
        if (!started) {
            return (false, startMsg);
        }

        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
        object grantPayload = grantPair
            ? new {
                mode = "workspace",
                relayUrl = baseUrl,
                sessionCode,
                pairSecret = pairHex,
                knockId = ComputeKnockChannelId(pairHex),
                tier = finalTier,
                ttlSeconds = finalTtl,
                biometricVerified,
                device = $"{Build.Manufacturer} {Build.Model}".Trim(),
                appVersion = $"v{ver} ({build})",
                approvedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }
            : new {
                mode = "session",
                relayUrl = baseUrl,
                sessionCode,
                pairSecret = "",
                knockId = ComputeKnockChannelId(pairHex),
                tier = finalTier,
                ttlSeconds = finalTtl,
                biometricVerified,
                device = $"{Build.Manufacturer} {Build.Model}".Trim(),
                appVersion = $"v{ver} ({build})",
                approvedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

        try {
            (string serverPubB64, object encGrant) = CreateEcdhP256GrantEnvelope(knockReq.ClientPubKeyB64, knockReq.KnockReqId, grantPayload);
            string devHash = ComputeWakeChannelHash(pairHex);
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/knock/approve");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new {
                    knockReqId = knockReq.KnockReqId,
                    serverPubKey = serverPubB64,
                    encryptedGrant = encGrant,
                    scope = grantPair ? "workspace" : "session"
                }),
                System.Text.Encoding.UTF8,
                "application/json");
            using HttpResponseMessage resp = await http.SendAsync(post, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                return (false, $"Smart Knock approval failed: HTTP {(int)resp.StatusCode}");
            }
            AppLogger.Info("CLOUD", $"Smart Knock approved (id={knockReq.KnockReqId}, sas={knockReq.SasCode}, scope={(grantPair ? "workspace" : "session")}, tier=T{finalTier}).");
            return (true, grantPair
                ? $"Connected & remembered (Tier {finalTier})"
                : $"Connected for {finalTtl / 60} min (Tier {finalTier})");
        } catch (Exception ex) {
            return (false, $"Smart Knock crypto/network error: {ex.Message}");
        }
    }

    internal static async Task DenyKnockRequestAsync(AppSettingsStore store, string knockReqId, string reason, int cooldownSeconds = 900) {
        pendingKnockRequests.TryRemove(knockReqId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex) || !TryNormalizeRelayUrl(store?.LiveRelayUrl, out string baseUrl)) {
            return;
        }
        try {
            string devHash = ComputeWakeChannelHash(pairHex);
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/knock/deny");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new {
                    knockReqId,
                    reason = reason ?? "Denied by user",
                    cooldownSeconds = Math.Clamp(cooldownSeconds, 60, 3600)
                }),
                System.Text.Encoding.UTF8,
                "application/json");
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage _ = await http.SendAsync(post, cts.Token).ConfigureAwait(false);
            AppLogger.Info("CLOUD", $"Smart Knock denied (id={knockReqId}, cooldown={cooldownSeconds}s): {reason}");
        } catch {
            // Best effort
        }
    }


    // ==================== ANDROID KEYSTORE HELPERS ====================

    static IKey GetOrCreatePairKeystoreKey() {
        KeyStore ks = KeyStore.GetInstance("AndroidKeyStore");
        ks.Load(null);
        if (ks.ContainsAlias(PairKeyAlias)) {
            IKey existing = ks.GetKey(PairKeyAlias, null);
            if (existing != null) {
                return existing;
            }
        }
        KeyGenerator kg = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
        KeyGenParameterSpec spec = new KeyGenParameterSpec.Builder(
                PairKeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();
        kg.Init(spec);
        return kg.GenerateKey();
    }

    static string EncryptWithKeystore(string plain) {
        try {
            IKey key = GetOrCreatePairKeystoreKey();
            Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
            byte[] iv = cipher.GetIV();
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plain);
            byte[] cipherBytes = cipher.DoFinal(plainBytes);
            return $"{KeystorePrefix}{Convert.ToBase64String(iv)}:{Convert.ToBase64String(cipherBytes)}";
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Live Bridge Keystore encrypt unavailable ({ex.GetType().Name})");
            return "";
        }
    }

    static string DecryptWithKeystore(string stored) {
        try {
            if (!stored.StartsWith(KeystorePrefix, StringComparison.Ordinal)) {
                return "";
            }
            string rest = stored[KeystorePrefix.Length..];
            int sep = rest.IndexOf(':');
            if (sep <= 0 || sep >= rest.Length - 1) {
                return "";
            }
            byte[] iv = Convert.FromBase64String(rest[..sep]);
            byte[] cipherBytes = Convert.FromBase64String(rest[(sep + 1)..]);
            IKey key = GetOrCreatePairKeystoreKey();
            Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
            byte[] plainBytes = cipher.DoFinal(cipherBytes);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Live Bridge Keystore decrypt failed ({ex.GetType().Name}); regenerate pairing key.");
            return "";
        }
    }

}
