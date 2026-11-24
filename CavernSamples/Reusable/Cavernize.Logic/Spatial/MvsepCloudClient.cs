using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Cavern.Neural;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Output container format requested from the official MVSEP REST API (<c>output_format</c> field).
/// </summary>
public enum MvsepOutputFormat {
    /// <summary>MP3 320 kbps (<c>output_format=0</c>).</summary>
    Mp3_320 = 0,

    /// <summary>Lossless 16-bit PCM WAV (<c>output_format=1</c> — zero external decoder dependency).</summary>
    Wav16Bit = 1,

    /// <summary>Lossless 16-bit FLAC (<c>output_format=2</c> — smaller download; decoded via FFmpeg delegate or WAV fallback).</summary>
    Flac16Bit = 2
}

/// <summary>
/// Curated official MVSEP REST API algorithm preset verified against <c>GET https://mvsep.com/api/app/algorithms</c>.
/// </summary>
public sealed class MvsepAlgorithmPreset {
    /// <summary>Stable preset identifier stored in settings.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable label for UI selectors.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Official MVSEP <c>sep_type</c> (<c>render_id</c>) value.</summary>
    public int SeparationType { get; init; }

    /// <summary>Optional MVSEP <c>add_opt1</c> sub-model selector (empty when unused).</summary>
    public string Opt1 { get; init; } = string.Empty;

    /// <summary>Optional MVSEP <c>add_opt2</c> overlap/mode selector (empty when unused).</summary>
    public string Opt2 { get; init; } = string.Empty;

    /// <summary>Optional MVSEP <c>add_opt3</c> selector (empty when unused).</summary>
    public string Opt3 { get; init; } = string.Empty;

    /// <summary>True when this preset returns multi-stem accompaniment (4/5/6+ stems) suitable for AI Spatial Upmix.</summary>
    public bool IsMultiStem { get; init; }

    /// <summary>True when this preset is an MVSEP Premium multi-model ensemble.</summary>
    public bool IsPremiumEnsemble { get; init; }

    /// <summary>Curated verified MVSEP REST presets (probed live against <c>https://mvsep.com/api/app/algorithms</c>).</summary>
    public static IReadOnlyList<MvsepAlgorithmPreset> Presets { get; } = [
        new MvsepAlgorithmPreset {
            Id = "mvsep_bs_roformer_vocals",
            DisplayName = "BS-RoFormer Vocals / Pure Instrumental (SDR 11.3)",
            SeparationType = 40,
            Opt1 = "29",
            Opt2 = "2",
            IsMultiStem = false
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_melband_roformer_vocals",
            DisplayName = "Mel-Band RoFormer Vocals / Instrumental (SDR 11.2)",
            SeparationType = 48,
            Opt1 = "1",
            Opt2 = "2",
            IsMultiStem = false
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_karaoke_lead_back",
            DisplayName = "MVSEP Karaoke 2-Stage (Lead + Backing + Pure Instrum)",
            SeparationType = 49,
            Opt1 = "3",
            Opt2 = "1",
            IsMultiStem = false
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_bs_roformer_6stem",
            DisplayName = "BS-RoFormer Multi 6-Stem (Vocals/Drums/Bass/Guitar/Piano/Other)",
            SeparationType = 63,
            Opt1 = "",
            Opt2 = "",
            IsMultiStem = true
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_scnet_xl_vocals",
            DisplayName = "SCNet XL Vocals / Instrumental (SDR 11.0)",
            SeparationType = 46,
            Opt1 = "2",
            Opt2 = "",
            IsMultiStem = false
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_demucs4_6stem",
            DisplayName = "Demucs4 HT 6-Stem (Full Band)",
            SeparationType = 20,
            Opt1 = "2",
            Opt2 = "",
            IsMultiStem = true
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_ensemble_vocals",
            DisplayName = "MVSEP Ensemble Vocals (BS+Mel RoFormer, Premium)",
            SeparationType = 26,
            Opt1 = "0",
            Opt2 = "10",
            IsMultiStem = false,
            IsPremiumEnsemble = true
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_ensemble_5stem",
            DisplayName = "MVSEP Ensemble 5-Stem (Premium)",
            SeparationType = 28,
            Opt1 = "",
            Opt2 = "",
            IsMultiStem = true,
            IsPremiumEnsemble = true
        },
        new MvsepAlgorithmPreset {
            Id = "mvsep_ensemble_all_in",
            DisplayName = "MVSEP Ensemble All-In (Lead+Back+6-Stem, Premium)",
            SeparationType = 30,
            Opt1 = "",
            Opt2 = "",
            IsMultiStem = true,
            IsPremiumEnsemble = true
        }
    ];

    /// <summary>Finds a curated preset by its stable <see cref="Id"/>, defaulting to BS-RoFormer Vocals.</summary>
    public static MvsepAlgorithmPreset FindOrDefault(string id) {
        if (!string.IsNullOrWhiteSpace(id)) {
            for (int i = 0; i < Presets.Count; i++) {
                if (string.Equals(Presets[i].Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) {
                    return Presets[i];
                }
            }
        }
        return Presets[0];
    }

    /// <summary>Selects the best MVSEP preset for a pipeline goal and Pure Instrumental / Backing preference.</summary>
    public static MvsepAlgorithmPreset ResolveForGoal(NeuralPipelineGoal goal, bool preserveBackingVocals, string preferredPresetId) {
        MvsepAlgorithmPreset preferred = FindOrDefault(preferredPresetId);
        if (goal == NeuralPipelineGoal.StemSeparation && !preferred.IsMultiStem) {
            return FindOrDefault("mvsep_bs_roformer_6stem");
        }
        if (goal == NeuralPipelineGoal.SpatialKaraoke) {
            if (preserveBackingVocals && preferred.SeparationType != 49 && preferred.SeparationType != 30) {
                return FindOrDefault("mvsep_karaoke_lead_back");
            }
            if (!preserveBackingVocals && preferred.SeparationType == 49) {
                // MVSEP Karaoke (49) with opt2=1 also emits instrum-only + vocals-lead + vocals-back, which supports both!
                return preferred;
            }
        }
        return preferred;
    }
}

/// <summary>
/// Summary of a single algorithm returned by <c>GET /api/app/algorithms</c>.
/// </summary>
public sealed class MvsepRemoteAlgorithmInfo {
    /// <summary>MVSEP <c>render_id</c>.</summary>
    public int RenderId { get; init; }

    /// <summary>Algorithm display name.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Stem artifact link returned inside <c>GET /api/separation/get?hash=...</c>.
/// </summary>
public sealed class MvsepStemDownloadLink {
    /// <summary>Stem role/type string reported by MVSEP (e.g., <c>vocals-lead</c>, <c>instrum-only</c>, <c>drums</c>).</summary>
    public string StemType { get; init; } = string.Empty;

    /// <summary>HTTPS download URL for the separated stem file.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Optional download filename.</summary>
    public string FileName { get; init; } = string.Empty;
}

/// <summary>
/// Status snapshot returned by <c>GET /api/separation/get?hash=...</c>.
/// </summary>
public sealed class MvsepJobStatus {
    /// <summary>Job hash.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Raw status string (<c>waiting</c>, <c>queued</c>, <c>processing</c>, <c>done</c>, <c>failed</c>).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Optional message or error string.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional server queue order (0 when active/done).</summary>
    public int QueueOrder { get; init; }

    /// <summary>Stem download links populated when <see cref="Status"/> is <c>done</c>.</summary>
    public IReadOnlyList<MvsepStemDownloadLink> Files { get; init; } = Array.Empty<MvsepStemDownloadLink>();

    /// <summary>True when the remote job completed successfully.</summary>
    public bool IsDone => string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the remote job failed or returned an unrecoverable error.</summary>
    public bool IsFailed =>
        string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration for <see cref="MvsepCloudClient"/>. Never logs or exposes <see cref="ApiToken"/>.
/// </summary>
public sealed class MvsepCloudOptions {
    /// <summary>Official primary MVSEP API endpoint.</summary>
    public const string DefaultBaseUrl = "https://mvsep.com/api";

    /// <summary>Official European MVSEP API mirror endpoint.</summary>
    public const string MirrorBaseUrl = "https://de.mvsep.com/api";

    /// <summary>Selected MVSEP regional API mirror (validated against official HTTPS host allowlist).</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>User-supplied MVSEP personal API token (stored locally in settings, never hardcoded or logged).</summary>
    public string ApiToken { get; init; } = string.Empty;

    /// <summary>Requested stem download format (default lossless 16-bit WAV for zero external decoder dependencies).</summary>
    public MvsepOutputFormat OutputFormat { get; init; } = MvsepOutputFormat.Wav16Bit;

    /// <summary>Initial polling interval in milliseconds (clamped to [50..10000] ms).</summary>
    public int PollIntervalMs { get; init; } = 2500;

    /// <summary>Maximum total wait duration for a remote job in seconds (default 900s = 15 min).</summary>
    public int MaxPollSeconds { get; init; } = 900;
}

/// <summary>
/// Official MVSEP REST API v1 client (<c>https://mvsep.com/api</c>) with strict HTTPS host allowlisting,
/// token redaction, lossless WAV upload/download, optional external codec decoder hook, and canonical
/// <see cref="NeuralSeparatedTrack"/> mapping for seamless integration into Cavern Spatial Karaoke, 3D Upmix, and CNTC v3 cache.
/// </summary>
public sealed class MvsepCloudClient : IDisposable {
    static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase) {
        "mvsep.com",
        "www.mvsep.com",
        "de.mvsep.com",
        "de2.mvsep.com",
        "sg.mvsep.com",
        "us.mvsep.com",
        "cdn.mvsep.com",
        "files.mvsep.com"
    };

    readonly HttpClient http;
    readonly bool ownsHttpClient;

    /// <summary>Creates a new MVSEP REST client using an optional injectable <see cref="HttpMessageHandler"/> for unit testing.</summary>
    public MvsepCloudClient(HttpMessageHandler handler = null) {
        ownsHttpClient = handler == null;
        http = handler != null
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CavernizeAndroid/3.0 (MVSEP-REST)");
    }

    /// <summary>
    /// Validates and normalizes an MVSEP API base URL against the official HTTPS host allowlist (SSRF protection).
    /// </summary>
    public static Uri ValidateOfficialBaseUri(string baseUrl) {
        string raw = string.IsNullOrWhiteSpace(baseUrl) ? MvsepCloudOptions.DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri uri)) {
            throw new ArgumentException("Invalid MVSEP API base URL.", nameof(baseUrl));
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("MVSEP API requires HTTPS.", nameof(baseUrl));
        }
        if (!IsAllowedMvsepHost(uri.Host)) {
            throw new ArgumentException($"Host '{uri.Host}' is not an official allowlisted MVSEP endpoint.", nameof(baseUrl));
        }
        return uri;
    }

    /// <summary>Returns true when <paramref name="host"/> is an official MVSEP domain or subdomain.</summary>
    public static bool IsAllowedMvsepHost(string host) {
        if (string.IsNullOrWhiteSpace(host)) {
            return false;
        }
        string h = host.Trim().TrimEnd('.');
        return AllowedHosts.Contains(h) || h.EndsWith(".mvsep.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Masks an API token for safe display/logging (never reveals full credential).</summary>
    public static string MaskToken(string token) {
        if (string.IsNullOrWhiteSpace(token)) {
            return "(empty)";
        }
        string t = token.Trim();
        if (t.Length <= 6) {
            return "••••••";
        }
        return $"{t.Substring(0, 3)}••••{t.Substring(t.Length - 3)}";
    }

    /// <summary>Redacts any occurrence of <paramref name="token"/> from diagnostic text.</summary>
    public static string RedactSecrets(string message, string token) {
        if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(token)) {
            return message ?? string.Empty;
        }
        return message.Replace(token.Trim(), "[REDACTED]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Queries <c>GET /api/app/algorithms</c> to verify live MVSEP API reachability and return available separation algorithms.
    /// </summary>
    public async Task<IReadOnlyList<MvsepRemoteAlgorithmInfo>> FetchAlgorithmsAsync(
        string baseUrl = MvsepCloudOptions.DefaultBaseUrl,
        CancellationToken token = default) {
        Uri root = ValidateOfficialBaseUri(baseUrl);
        string url = $"{root}/app/algorithms";
        using HttpResponseMessage resp = await http.GetAsync(url, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        List<MvsepRemoteAlgorithmInfo> list = [];
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement rootEl = doc.RootElement;
        if (rootEl.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in rootEl.EnumerateArray()) {
                int id = item.TryGetProperty("render_id", out JsonElement idEl) && idEl.TryGetInt32(out int parsedId) ? parsedId : -1;
                string name = item.TryGetProperty("name", out JsonElement nameEl) ? (nameEl.GetString() ?? string.Empty) : string.Empty;
                if (id >= 0) {
                    list.Add(new MvsepRemoteAlgorithmInfo { RenderId = id, Name = name });
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Submits a separation job to <c>POST /api/separation/create</c> and returns the remote job hash.
    /// </summary>
    public async Task<string> CreateSeparationAsync(
        byte[] audioBytes,
        string fileName,
        MvsepAlgorithmPreset preset,
        MvsepCloudOptions options,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(audioBytes);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(options);

        string apiToken = (options.ApiToken ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(apiToken)) {
            throw new InvalidOperationException("MVSEP API token is required for Cloud Offload.");
        }
        Uri root = ValidateOfficialBaseUri(options.BaseUrl);
        string endpoint = $"{root}/separation/create";
        string safeFileName = string.IsNullOrWhiteSpace(fileName) ? "track.wav" : Path.GetFileName(fileName);

        try {
            using MultipartFormDataContent form = new();
            ByteArrayContent fileContent = new(audioBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                safeFileName.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ? "audio/flac" : "audio/wav");
            form.Add(fileContent, "audiofile", safeFileName);
            form.Add(new StringContent(apiToken), "api_token");
            form.Add(new StringContent(preset.SeparationType.ToString(CultureInfo.InvariantCulture)), "sep_type");
            form.Add(new StringContent(((int)options.OutputFormat).ToString(CultureInfo.InvariantCulture)), "output_format");
            if (!string.IsNullOrWhiteSpace(preset.Opt1)) {
                form.Add(new StringContent(preset.Opt1), "add_opt1");
            }
            if (!string.IsNullOrWhiteSpace(preset.Opt2)) {
                form.Add(new StringContent(preset.Opt2), "add_opt2");
            }
            if (!string.IsNullOrWhiteSpace(preset.Opt3)) {
                form.Add(new StringContent(preset.Opt3), "add_opt3");
            }

            using HttpResponseMessage resp = await http.PostAsync(endpoint, form, token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                throw new InvalidOperationException(
                    $"MVSEP create failed (HTTP {(int)resp.StatusCode}): {RedactSecrets(Truncate(body, 220), apiToken)}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement rootEl = doc.RootElement;
            bool ok = !rootEl.TryGetProperty("success", out JsonElement okEl) || okEl.ValueKind != JsonValueKind.False;
            string hash = string.Empty;
            if (rootEl.TryGetProperty("data", out JsonElement dataEl) && dataEl.ValueKind == JsonValueKind.Object) {
                if (dataEl.TryGetProperty("hash", out JsonElement hashEl)) {
                    hash = hashEl.GetString() ?? string.Empty;
                }
                if (!ok && dataEl.TryGetProperty("message", out JsonElement msgEl)) {
                    throw new InvalidOperationException($"MVSEP rejected request: {RedactSecrets(msgEl.GetString(), apiToken)}");
                }
            }
            if (string.IsNullOrWhiteSpace(hash)) {
                string errMsg = rootEl.TryGetProperty("message", out JsonElement topMsg) ? topMsg.GetString() : "Missing job hash";
                throw new InvalidOperationException($"MVSEP create failed: {RedactSecrets(errMsg, apiToken)}");
            }
            return hash;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException(RedactSecrets(ex.Message, apiToken), ex);
        }
    }

    /// <summary>
    /// Queries <c>GET /api/separation/get?hash=...</c> once and parses job status and stem download links.
    /// </summary>
    public async Task<MvsepJobStatus> GetSeparationStatusAsync(
        string hash,
        MvsepCloudOptions options,
        CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(hash)) {
            throw new ArgumentException("Job hash is required.", nameof(hash));
        }
        ArgumentNullException.ThrowIfNull(options);

        Uri root = ValidateOfficialBaseUri(options.BaseUrl);
        string apiToken = (options.ApiToken ?? string.Empty).Trim();
        string url = $"{root}/separation/get?hash={Uri.EscapeDataString(hash.Trim())}";

        try {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            using HttpResponseMessage resp = await http.SendAsync(req, token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                throw new InvalidOperationException(
                    $"MVSEP status query failed (HTTP {(int)resp.StatusCode}): {RedactSecrets(Truncate(body, 200), apiToken)}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement rootEl = doc.RootElement;
            string status = rootEl.TryGetProperty("status", out JsonElement stEl) ? (stEl.GetString() ?? string.Empty) : string.Empty;
            string message = string.Empty;
            int queueOrder = 0;
            List<MvsepStemDownloadLink> files = [];

            if (rootEl.TryGetProperty("data", out JsonElement dataEl) && dataEl.ValueKind == JsonValueKind.Object) {
                if (string.IsNullOrEmpty(status) && dataEl.TryGetProperty("status", out JsonElement dSt)) {
                    status = dSt.GetString() ?? string.Empty;
                }
                if (dataEl.TryGetProperty("message", out JsonElement dMsg)) {
                    message = dMsg.GetString() ?? string.Empty;
                }
                if (dataEl.TryGetProperty("current_order", out JsonElement ordEl) && ordEl.TryGetInt32(out int parsedOrd)) {
                    queueOrder = parsedOrd;
                }
                if (dataEl.TryGetProperty("files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement f in filesEl.EnumerateArray()) {
                        string fUrl = f.TryGetProperty("url", out JsonElement uEl) ? (uEl.GetString() ?? string.Empty) : string.Empty;
                        string fType = f.TryGetProperty("type", out JsonElement tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
                        string fName = f.TryGetProperty("download", out JsonElement nEl) ? (nEl.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrEmpty(fName) && !string.IsNullOrEmpty(fUrl) && Uri.TryCreate(fUrl, UriKind.Absolute, out Uri parsedUrl)) {
                            fName = Path.GetFileName(parsedUrl.AbsolutePath);
                        }
                        if (!string.IsNullOrWhiteSpace(fUrl)) {
                            files.Add(new MvsepStemDownloadLink {
                                StemType = fType,
                                Url = fUrl,
                                FileName = fName
                            });
                        }
                    }
                }
            }

            return new MvsepJobStatus {
                Hash = hash.Trim(),
                Status = status,
                Message = RedactSecrets(message, apiToken),
                QueueOrder = queueOrder,
                Files = files
            };
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException(RedactSecrets(ex.Message, apiToken), ex);
        }
    }

    /// <summary>
    /// Downloads a separated stem file from an allowlisted HTTPS MVSEP URL.
    /// </summary>
    public async Task<byte[]> DownloadStemBytesAsync(string downloadUrl, CancellationToken token = default) {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedMvsepHost(uri.Host)) {
            throw new InvalidOperationException("Refused stem download from non-allowlisted or non-HTTPS host.");
        }
        using HttpResponseMessage resp = await http.GetAsync(uri, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the full MVSEP Cloud Offload pipeline: encodes stereo PCM to lossless WAV, uploads to MVSEP,
    /// polls until completion with cancellation support, downloads all returned stems, decodes them into
    /// planar stereo buffers, and returns a canonical <see cref="NeuralSeparatedTrack"/> ready for playback &amp; CNTC v3 cache.
    /// </summary>
    public async Task<NeuralSeparatedTrack> SeparateTrackAsync(
        string sourceTrackKey,
        float[] mixLeft,
        float[] mixRight,
        int sampleRate,
        MvsepAlgorithmPreset preset,
        MvsepCloudOptions options,
        string displayTitle = "",
        string artistName = "",
        Func<byte[], string, (float[] Left, float[] Right, int SampleRate)> externalDecoder = null,
        Action<double, string> onProgress = null,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(mixLeft);
        ArgumentNullException.ThrowIfNull(mixRight);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(options);

        int sr = sampleRate >= 8000 ? sampleRate : 44100;
        onProgress?.Invoke(0.05, $"MVSEP Cloud: Encoding upload ({preset.DisplayName})...");
        byte[] wavPayload = EncodeStereoWav16(mixLeft, mixRight, sr);

        onProgress?.Invoke(0.15, $"MVSEP Cloud: Uploading ({wavPayload.Length / (1024.0 * 1024.0):0.0} MB)...");
        string hash = await CreateSeparationAsync(wavPayload, "cavernize_input.wav", preset, options, token).ConfigureAwait(false);

        int basePollMs = Math.Clamp(options.PollIntervalMs, 20, 10000);
        DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(options.MaxPollSeconds, 5, 3600));
        MvsepJobStatus status = null;
        int pollStep = 0;

        while (true) {
            token.ThrowIfCancellationRequested();
            status = await GetSeparationStatusAsync(hash, options, token).ConfigureAwait(false);
            if (status.IsDone) {
                break;
            }
            if (status.IsFailed) {
                string err = string.IsNullOrWhiteSpace(status.Message) ? "Remote separation failed." : status.Message;
                throw new InvalidOperationException($"MVSEP Cloud failed: {err}");
            }
            if (DateTime.UtcNow >= deadlineUtc) {
                throw new TimeoutException($"MVSEP Cloud job timed out after {options.MaxPollSeconds}s.");
            }
            pollStep++;
            double pollProg = Math.Min(0.72, 0.22 + pollStep * 0.04);
            string qText = status.QueueOrder > 0 ? $" (queue #{status.QueueOrder})" : string.Empty;
            onProgress?.Invoke(pollProg, $"MVSEP Cloud: {status.Status}{qText}...");
            int delayMs = Math.Min(6000, basePollMs + Math.Min(pollStep, 6) * (basePollMs / 4));
            await Task.Delay(delayMs, token).ConfigureAwait(false);
        }

        if (status.Files == null || status.Files.Count == 0) {
            throw new InvalidOperationException("MVSEP Cloud completed but returned zero stem files.");
        }

        List<StereoStemBuffer> stems = new(status.Files.Count);
        int targetLen = Math.Min(mixLeft.Length, mixRight.Length);
        int outSr = sr;

        for (int i = 0; i < status.Files.Count; i++) {
            token.ThrowIfCancellationRequested();
            MvsepStemDownloadLink link = status.Files[i];
            string canonicalName = NormalizeMvsepStemName(link.StemType, link.FileName, i);
            // Skip redundant combined back+instrum stem when we already have vocals-lead, vocals-back, and instrum-only
            if (string.Equals(canonicalName, "back_instrumental", StringComparison.OrdinalIgnoreCase) &&
                status.Files.Count >= 4) {
                continue;
            }

            double dlProg = 0.74 + 0.24 * (i / (double)status.Files.Count);
            onProgress?.Invoke(dlProg, $"MVSEP Cloud: Downloading {canonicalName} ({i + 1}/{status.Files.Count})...");
            byte[] stemBytes = await DownloadStemBytesAsync(link.Url, token).ConfigureAwait(false);

            float[] sL;
            float[] sR;
            int decodedSr;
            if (TryDecodePcmWavStereo(stemBytes, out sL, out sR, out decodedSr)) {
                outSr = decodedSr;
            } else if (externalDecoder != null) {
                (sL, sR, decodedSr) = externalDecoder(stemBytes, link.FileName);
                if (decodedSr >= 8000) {
                    outSr = decodedSr;
                }
            } else {
                throw new NotSupportedException($"MVSEP returned non-WAV stem '{link.FileName}' without an external audio decoder.");
            }

            // Align length cleanly to mix length when same sample rate
            if (outSr == sr && sL.Length != targetLen) {
                float[] alignedL = new float[targetLen];
                float[] alignedR = new float[targetLen];
                int copy = Math.Min(targetLen, Math.Min(sL.Length, sR.Length));
                Array.Copy(sL, alignedL, copy);
                Array.Copy(sR, alignedR, copy);
                sL = alignedL;
                sR = alignedR;
            }
            stems.Add(new StereoStemBuffer(canonicalName, sL, sR));
        }

        // If MVSEP Karaoke returned lead_vocal + backing_vocals without a combined vocals stem, synthesize combined vocals
        // so toggling between Lead-Only Karaoke and Pure Instrumental (Remove Backing) is instant!
        StereoStemBuffer leadStem = stems.FirstOrDefault(s => string.Equals(s.Name, "lead_vocal", StringComparison.OrdinalIgnoreCase));
        StereoStemBuffer backStem = stems.FirstOrDefault(s => string.Equals(s.Name, "backing_vocals", StringComparison.OrdinalIgnoreCase));
        bool hasCombinedVocals = stems.Any(s => string.Equals(s.Name, "vocals", StringComparison.OrdinalIgnoreCase));
        if (leadStem != null && backStem != null && !hasCombinedVocals && leadStem.Length == backStem.Length) {
            float[] combL = new float[leadStem.Length];
            float[] combR = new float[leadStem.Length];
            for (int n = 0; n < leadStem.Length; n++) {
                combL[n] = leadStem.Left[n] + backStem.Left[n];
                combR[n] = leadStem.Right[n] + backStem.Right[n];
            }
            stems.Add(new StereoStemBuffer("vocals", combL, combR));
        }

        string planSig = $"mvsep_s{preset.SeparationType}_o1{preset.Opt1}_o2{preset.Opt2}";
        NeuralSeparatedTrack separated = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
            sourceTrackKey,
            outSr,
            stems.ToArray(),
            dereverb: null,
            derivedFromLosslessTwin: false,
            planSignature: planSig);
        separated.DisplayTitle = displayTitle ?? string.Empty;
        separated.ArtistName = artistName ?? string.Empty;
        separated.OriginTag = "MVSEP Cloud";
        separated.ModelSummary = preset.DisplayName;

        onProgress?.Invoke(1.0, $"MVSEP Cloud Ready ({preset.DisplayName})");
        return separated;
    }

    /// <summary>Maps MVSEP API stem labels and filenames into Cavern canonical stem names.</summary>
    public static string NormalizeMvsepStemName(string stemType, string fileName, int fallbackIndex) {
        string raw = (!string.IsNullOrWhiteSpace(stemType) ? stemType : Path.GetFileNameWithoutExtension(fileName ?? string.Empty))
            .Trim()
            .ToLowerInvariant();

        if (raw.Contains("vocals-lead") || raw.Contains("lead_vocal") || raw.Contains("lead vocal") || raw == "lead") {
            return "lead_vocal";
        }
        if (raw.Contains("vocals-back") || raw.Contains("backing_vocal") || raw.Contains("backing") || raw.Contains("back_vocal")) {
            return "backing_vocals";
        }
        if (raw.Contains("back-instrum") || raw.Contains("backing+instrum")) {
            return "back_instrumental";
        }
        if (raw.Contains("instrum-only") || raw.Contains("instrumental") || raw.Contains("instrum") || raw.Contains("no_vocals")) {
            return "instrumental";
        }
        if (raw.Contains("vocal")) {
            return "vocals";
        }
        if (raw.Contains("drum")) {
            return "drums";
        }
        if (raw.Contains("bass")) {
            return "bass";
        }
        if (raw.Contains("guitar")) {
            return "guitar";
        }
        if (raw.Contains("piano") || raw.Contains("key")) {
            return "piano";
        }
        if (raw.Contains("other")) {
            return "other";
        }
        return string.IsNullOrWhiteSpace(raw) ? $"stem_{fallbackIndex}" : raw;
    }

    /// <summary>Encodes planar stereo float32 audio into a standard RIFF PCM 16-bit WAV byte array.</summary>
    public static byte[] EncodeStereoWav16(float[] left, float[] right, int sampleRate) {
        int frames = Math.Min(left?.Length ?? 0, right?.Length ?? 0);
        int sr = sampleRate >= 8000 ? sampleRate : 44100;
        int dataBytes = frames * 2 * sizeof(short);
        byte[] wav = new byte[44 + dataBytes];

        Encoding.ASCII.GetBytes("RIFF", 0, 4, wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE", 0, 4, wav, 8);
        Encoding.ASCII.GetBytes("fmt ", 0, 4, wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), 2); // Stereo
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sr);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), sr * 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data", 0, 4, wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);

        int offset = 44;
        for (int i = 0; i < frames; i++) {
            short l16 = FloatToInt16(left[i]);
            short r16 = FloatToInt16(right[i]);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset, 2), l16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset + 2, 2), r16);
            offset += 4;
        }
        return wav;
    }

    /// <summary>Decodes an uncompressed RIFF WAV (16-bit, 24-bit, or 32-bit float; mono or stereo) into planar stereo float arrays.</summary>
    public static bool TryDecodePcmWavStereo(byte[] wavBytes, out float[] left, out float[] right, out int sampleRate) {
        left = null;
        right = null;
        sampleRate = 44100;
        if (wavBytes == null || wavBytes.Length < 44) {
            return false;
        }
        if (wavBytes[0] != 'R' || wavBytes[1] != 'I' || wavBytes[2] != 'F' || wavBytes[3] != 'F' ||
            wavBytes[8] != 'W' || wavBytes[9] != 'A' || wavBytes[10] != 'V' || wavBytes[11] != 'E') {
            return false;
        }

        int pos = 12;
        ushort audioFormat = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        int sr = 0;
        int dataOffset = -1;
        int dataLen = 0;

        while (pos + 8 <= wavBytes.Length) {
            string chunkId = Encoding.ASCII.GetString(wavBytes, pos, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(pos + 4, 4));
            pos += 8;
            if (chunkSize < 0 || pos + chunkSize > wavBytes.Length) {
                chunkSize = Math.Max(0, wavBytes.Length - pos);
            }
            if (chunkId == "fmt " && chunkSize >= 16) {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(pos, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(pos + 2, 2));
                sr = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(pos + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(pos + 14, 2));
                if (audioFormat == 0xFFFE && chunkSize >= 40) {
                    ushort subFmt = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(pos + 24, 2));
                    audioFormat = subFmt;
                }
            } else if (chunkId == "data") {
                dataOffset = pos;
                dataLen = chunkSize;
                break;
            }
            pos += chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || channels < 1 || channels > 2 || sr < 8000 || (audioFormat != 1 && audioFormat != 3)) {
            return false;
        }
        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample < 2 || bytesPerSample > 4) {
            return false;
        }
        int frameStride = bytesPerSample * channels;
        int frameCount = dataLen / frameStride;
        if (frameCount <= 0) {
            return false;
        }

        left = new float[frameCount];
        right = new float[frameCount];
        sampleRate = sr;

        for (int i = 0; i < frameCount; i++) {
            int fPos = dataOffset + i * frameStride;
            float l = ReadSample(wavBytes, fPos, audioFormat, bitsPerSample);
            float r = channels >= 2 ? ReadSample(wavBytes, fPos + bytesPerSample, audioFormat, bitsPerSample) : l;
            left[i] = l;
            right[i] = r;
        }
        return true;
    }

    static float ReadSample(byte[] buf, int pos, ushort audioFormat, ushort bitsPerSample) {
        if (audioFormat == 3 && bitsPerSample == 32) {
            float v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4)));
            return float.IsFinite(v) ? v : 0f;
        }
        if (bitsPerSample == 16) {
            return BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(pos, 2)) / 32768f;
        }
        if (bitsPerSample == 24) {
            int v = buf[pos] | (buf[pos + 1] << 8) | ((sbyte)buf[pos + 2] << 16);
            return v / 8388608f;
        }
        if (bitsPerSample == 32) {
            return BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4)) / 2147483648f;
        }
        return 0f;
    }

    static short FloatToInt16(float sample) {
        if (!float.IsFinite(sample)) {
            return 0;
        }
        float clamped = Math.Clamp(sample, -1f, 1f);
        int q = (int)MathF.Round(clamped * 32767f);
        return (short)Math.Clamp(q, short.MinValue, short.MaxValue);
    }

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...");

    /// <inheritdoc/>
    public void Dispose() {
        if (ownsHttpClient) {
            http.Dispose();
        }
    }
}
