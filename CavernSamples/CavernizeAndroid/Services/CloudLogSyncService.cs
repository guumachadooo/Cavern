using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Android.Content;
using Android.Security.Keystore;

using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace CavernizeAndroid.Services;

public sealed class CloudLogSyncResult {
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public int BytesUploaded { get; init; }
    public int HttpStatusCode { get; init; }
    public DateTime SyncedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Uploads sanitized diagnostic logs and session summary snapshots directly to GitHub via the
/// GitHub REST Contents API, using a user-provided Fine-Grained Personal Access Token encrypted
/// at rest with Android Keystore (AES-256-GCM).
/// </summary>
public static class CloudLogSyncService {
    public const string DefaultRepo = "guumachadooo/Cavern";
    public const string DiagnosticsBranch = "diagnostics";
    public const string DiagnosticsLogPath = "device-logs/cavernize_log.txt";
    public const string DiagnosticsSummaryPath = "device-logs/latest_summary.json";
    public const string DiagnosticsSelfCheckMdPath = "device-logs/latest_selfcheck.md";
    public const string DiagnosticsSelfCheckJsonPath = "device-logs/latest_selfcheck.json";
    public const string MasterLogPath = ".agents/device_logs/cavernize_log.txt";
    public const string GitHubTokenCreateUrl = "https://github.com/settings/personal-access-tokens/new";

    const string KeyAlias = "cavernize_cloud_log_pat_v1";
    const string KeystorePrefix = "ks1:";
    const int MaxUploadBytes = 768 * 1024;
    const int AutoErrorDebounceMs = 15_000;
    static readonly TimeSpan AutoErrorMinInterval = TimeSpan.FromMinutes(5);

    static readonly Regex RepoFormatRegex = new(@"^[A-Za-z0-9_.-]{1,64}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
    static readonly HttpClient http = BuildClient();
    static readonly SemaphoreSlim uploadGate = new(1, 1);
    static readonly object autoSyncLock = new();

    static DateTime lastAutoErrorSyncUtc = DateTime.MinValue;
    static CancellationTokenSource pendingAutoErrorCts;

    static HttpClient BuildClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.9"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public static bool HasStoredToken(AppSettingsStore store) =>
        store != null && !string.IsNullOrWhiteSpace(store.CloudLogEncryptedToken);

    public static bool IsConfigured(AppSettingsStore store) =>
        store != null && store.CloudLogSyncEnabled && HasStoredToken(store) && TryParseRepo(store.CloudLogRepo, out _, out _);

    public static (string branch, string logPath, string summaryPath) ResolveTarget(AppSettingsStore store) {
        if (store != null && store.CloudLogBranchMode == 1) {
            return ("master", MasterLogPath, null);
        }
        return (DiagnosticsBranch, DiagnosticsLogPath, DiagnosticsSummaryPath);
    }

    public static bool TryParseRepo(string rawRepo, out string owner, out string repo) {
        owner = "";
        repo = "";
        string clean = (rawRepo ?? "").Trim().Trim('/');
        if (clean.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) {
            clean = clean["https://github.com/".Length..].Trim('/');
        }
        if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
            clean = clean[..^4];
        }
        if (!RepoFormatRegex.IsMatch(clean) || clean.Contains("..", StringComparison.Ordinal)) {
            return false;
        }
        int slash = clean.IndexOf('/');
        if (slash <= 0 || slash >= clean.Length - 1) {
            return false;
        }
        owner = clean[..slash];
        repo = clean[(slash + 1)..];
        return true;
    }

    public static bool SaveToken(AppSettingsStore store, string plainToken) {
        if (store == null) {
            return false;
        }
        string clean = (plainToken ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) {
            ClearToken(store);
            return true;
        }
        string encrypted = EncryptTokenWithKeystore(clean);
        if (string.IsNullOrEmpty(encrypted)) {
            return false;
        }
        store.CloudLogEncryptedToken = encrypted;
        return true;
    }

    public static void ClearToken(AppSettingsStore store) {
        if (store != null) {
            store.CloudLogEncryptedToken = "";
            store.CloudLogSyncEnabled = false;
        }
    }

    public static async Task<CloudLogSyncResult> TestConnectionAsync(
        AppSettingsStore store,
        string overrideRepo = null,
        string overridePlainToken = null,
        CancellationToken ct = default) {
        string repoText = string.IsNullOrWhiteSpace(overrideRepo) ? (store?.CloudLogRepo ?? DefaultRepo) : overrideRepo;
        if (!TryParseRepo(repoText, out string owner, out string repo)) {
            return new CloudLogSyncResult { Success = false, Message = "Invalid repository format (expected owner/repo)." };
        }

        string token = !string.IsNullOrWhiteSpace(overridePlainToken)
            ? overridePlainToken.Trim()
            : LoadToken(store);
        if (string.IsNullOrWhiteSpace(token)) {
            return new CloudLogSyncResult { Success = false, Message = "No GitHub token configured." };
        }

        try {
            string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
            using HttpRequestMessage req = CreateAuthedRequest(HttpMethod.Get, url, token);
            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode) {
                return new CloudLogSyncResult {
                    Success = false,
                    HttpStatusCode = code,
                    Message = FormatHttpFailure(resp.StatusCode, "Repository access failed")
                };
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string fullName = $"{owner}/{repo}";
            bool canPush = true;
            try {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("full_name", out JsonElement fnEl)) {
                    fullName = fnEl.GetString() ?? fullName;
                }
                if (doc.RootElement.TryGetProperty("permissions", out JsonElement perms) &&
                    perms.TryGetProperty("push", out JsonElement pushEl) &&
                    pushEl.ValueKind == JsonValueKind.False) {
                    canPush = false;
                }
            } catch {
                // Keep defaults
            }

            if (!canPush) {
                return new CloudLogSyncResult {
                    Success = false,
                    HttpStatusCode = code,
                    Message = $"Connected to {fullName}, but token lacks Contents write permission."
                };
            }

            return new CloudLogSyncResult {
                Success = true,
                HttpStatusCode = code,
                Message = $"Connected to {fullName} (write permission verified)."
            };
        } catch (System.OperationCanceledException) {
            return new CloudLogSyncResult { Success = false, Message = "Connection test cancelled." };
        } catch (Exception ex) {
            return new CloudLogSyncResult { Success = false, Message = $"Network error: {ex.Message}" };
        }
    }

    public static async Task<CloudLogSyncResult> UploadLogsAsync(
        Context context,
        AppSettingsStore store,
        string triggerReason = "manual",
        CancellationToken ct = default) {
        if (store == null) {
            return new CloudLogSyncResult { Success = false, Message = "Settings store unavailable." };
        }
        if (!TryParseRepo(store.CloudLogRepo, out string owner, out string repo)) {
            return new CloudLogSyncResult { Success = false, Message = "Invalid repository format (expected owner/repo)." };
        }
        string token = LoadToken(store);
        if (string.IsNullOrWhiteSpace(token)) {
            return new CloudLogSyncResult { Success = false, Message = "GitHub token not configured." };
        }

        await uploadGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            (string branch, string logPath, string summaryPath) = ResolveTarget(store);
            (string ver, long buildNo) = context != null
                ? AppUpdateService.GetInstalledVersion(context)
                : ("2.9", 0);

            if (string.Equals(branch, DiagnosticsBranch, StringComparison.Ordinal)) {
                (bool okBranch, string branchErr) = await EnsureBranchExistsAsync(owner, repo, branch, token, ct).ConfigureAwait(false);
                if (!okBranch) {
                    return RecordOutcome(store, new CloudLogSyncResult {
                        Success = false,
                        TargetBranch = branch,
                        TargetPath = logPath,
                        Message = branchErr
                    });
                }
            }

            string logText = AppLogger.ReadTailUtf8(MaxUploadBytes);
            if (string.IsNullOrWhiteSpace(logText)) {
                logText = $"(Empty log at {DateTime.UtcNow:O} | sid={AppLogger.SessionId}){System.Environment.NewLine}";
            }
            byte[] logBytes = System.Text.Encoding.UTF8.GetBytes(logText);
            string commitMsg = $"chore(logs): sync Cavernize v{ver} (b{buildNo}, {triggerReason}, sid={AppLogger.SessionId}) [skip ci]";

            CloudLogSyncResult mainRes = await PutRepoFileWithRetryAsync(
                owner, repo, branch, logPath, logBytes, commitMsg, token, ct).ConfigureAwait(false);

            if (!mainRes.Success) {
                AppLogger.Warn("CLOUD", $"Cloud log upload failed ({branch}:{logPath}): {mainRes.Message}");
                return RecordOutcome(store, mainRes);
            }

            if (!string.IsNullOrEmpty(summaryPath)) {
                try {
                    string summaryJson = AppLogger.BuildDiagnosticSummaryJson(context, store);
                    byte[] summaryBytes = System.Text.Encoding.UTF8.GetBytes(summaryJson);
                    string summaryMsg = $"chore(logs): update diagnostic summary v{ver} ({triggerReason}) [skip ci]";
                    _ = await PutRepoFileWithRetryAsync(
                        owner, repo, branch, summaryPath, summaryBytes, summaryMsg, token, ct).ConfigureAwait(false);
                } catch {
                    // Companion summary is best-effort; main log already uploaded cleanly
                }
            }

            if (string.Equals(triggerReason, "crash", StringComparison.OrdinalIgnoreCase) || AppLogger.HasPendingCrashReport) {
                AppLogger.ClearPendingCrashMarker();
            }

            AppLogger.Info("CLOUD", $"Uploaded diagnostic log ({logBytes.Length / 1024.0:0.0} KB) to {owner}/{repo}@{branch}:{logPath} ({triggerReason})");
            return RecordOutcome(store, mainRes);
        } catch (System.OperationCanceledException) {
            return new CloudLogSyncResult { Success = false, Message = "Upload cancelled." };
        } catch (Exception ex) {
            string msg = AppLogger.SanitizeSensitiveText($"Upload error: {ex.Message}");
            AppLogger.Warn("CLOUD", msg);
            return RecordOutcome(store, new CloudLogSyncResult { Success = false, Message = msg });
        } finally {
            uploadGate.Release();
        }
    }

    /// <summary>
    /// Uploads a Self-Check report (Markdown + JSON) directly to the diagnostics branch alongside the active device log.
    /// </summary>
    public static async Task<CloudLogSyncResult> UploadSelfCheckReportAsync(
        Context context,
        AppSettingsStore store,
        SelfCheckReport report,
        string triggerReason = "selfcheck",
        CancellationToken ct = default) {
        if (store == null || report == null) {
            return new CloudLogSyncResult { Success = false, Message = "Self-check report unavailable." };
        }
        if (!TryParseRepo(store.CloudLogRepo, out string owner, out string repo)) {
            return new CloudLogSyncResult { Success = false, Message = "Invalid repository format. Use owner/repo." };
        }
        string token = LoadToken(store);
        if (string.IsNullOrEmpty(token)) {
            return new CloudLogSyncResult { Success = false, Message = "No GitHub token configured." };
        }

        await uploadGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            (string branch, _, _) = ResolveTarget(store);
            // Always publish self-check artifacts to the dedicated diagnostics branch so master stays conflict-free
            string targetBranch = string.Equals(branch, "master", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticsBranch
                : branch;

            (bool branchOk, string branchErr) = await EnsureBranchExistsAsync(owner, repo, targetBranch, token, ct).ConfigureAwait(false);
            if (!branchOk) {
                return RecordOutcome(store, new CloudLogSyncResult { Success = false, TargetBranch = targetBranch, Message = branchErr });
            }

            string mdText = AppLogger.SanitizeSensitiveText(report.ToMarkdown());
            byte[] mdBytes = System.Text.Encoding.UTF8.GetBytes(mdText);
            string commitMsg = $"chore(selfcheck): {report.OverallVerdict} {report.Mode} v{report.AppVersion} ({triggerReason}) [skip ci]";

            CloudLogSyncResult mdRes = await PutRepoFileWithRetryAsync(
                owner, repo, targetBranch, DiagnosticsSelfCheckMdPath, mdBytes, commitMsg, token, ct).ConfigureAwait(false);
            if (!mdRes.Success) {
                return RecordOutcome(store, mdRes);
            }

            try {
                string jsonText = AppLogger.SanitizeSensitiveText(report.ToJson());
                byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(jsonText);
                _ = await PutRepoFileWithRetryAsync(
                    owner, repo, targetBranch, DiagnosticsSelfCheckJsonPath, jsonBytes,
                    $"chore(selfcheck): update json v{report.AppVersion} ({triggerReason}) [skip ci]", token, ct).ConfigureAwait(false);
            } catch {
                // Best effort JSON companion
            }

            // Also refresh device log tail on the same branch so AI has full context
            try {
                string logTail = AppLogger.ReadTailUtf8(MaxUploadBytes);
                if (!string.IsNullOrWhiteSpace(logTail)) {
                    byte[] logBytes = System.Text.Encoding.UTF8.GetBytes(logTail);
                    _ = await PutRepoFileWithRetryAsync(
                        owner, repo, targetBranch, DiagnosticsLogPath, logBytes,
                        $"chore(logs): sync with selfcheck {report.OverallVerdict} v{report.AppVersion} [skip ci]", token, ct).ConfigureAwait(false);
                }
            } catch {
                // Best effort log companion
            }

            AppLogger.Info("CLOUD", $"Uploaded Self-Check ({report.OverallVerdict}) to {owner}/{repo}@{targetBranch}:{DiagnosticsSelfCheckMdPath}");
            return RecordOutcome(store, mdRes);
        } catch (System.OperationCanceledException) {
            return new CloudLogSyncResult { Success = false, Message = "Self-check upload cancelled." };
        } catch (Exception ex) {
            string msg = AppLogger.SanitizeSensitiveText($"Self-check upload error: {ex.Message}");
            AppLogger.Warn("CLOUD", msg);
            return RecordOutcome(store, new CloudLogSyncResult { Success = false, Message = msg });
        } finally {
            uploadGate.Release();
        }
    }

    /// <summary>
    /// Called once on startup after AppLogger + AppSettingsStore initialize.
    /// Automatically uploads the previous session's fatal crash log if configured.
    /// </summary>
    public static void TriggerStartupCrashSyncIfNeeded(Context context, AppSettingsStore store, Action<CloudLogSyncResult> onCompleted = null) {
        if (!IsConfigured(store) || !store.CloudLogAutoOnCrash || !AppLogger.HasPendingCrashReport) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                await Task.Delay(2500).ConfigureAwait(false);
                CloudLogSyncResult res = await UploadLogsAsync(context, store, "crash").ConfigureAwait(false);
                onCompleted?.Invoke(res);
            } catch {
                // Best-effort background sync
            }
        });
    }

    /// <summary>
    /// Debounces bursts of runtime errors into a single throttled upload (minimum 5-minute cooldown).
    /// </summary>
    public static void ScheduleAutoErrorSyncIfEnabled(Context context, AppSettingsStore store, Action<CloudLogSyncResult> onCompleted = null) {
        if (!IsConfigured(store) || !store.CloudLogAutoOnError) {
            return;
        }
        CancellationTokenSource localCts;
        lock (autoSyncLock) {
            if (DateTime.UtcNow - lastAutoErrorSyncUtc < AutoErrorMinInterval) {
                return;
            }
            pendingAutoErrorCts?.Cancel();
            pendingAutoErrorCts?.Dispose();
            pendingAutoErrorCts = new CancellationTokenSource();
            localCts = pendingAutoErrorCts;
        }

        _ = Task.Run(async () => {
            try {
                await Task.Delay(AutoErrorDebounceMs, localCts.Token).ConfigureAwait(false);
                lock (autoSyncLock) {
                    if (DateTime.UtcNow - lastAutoErrorSyncUtc < AutoErrorMinInterval) {
                        return;
                    }
                    lastAutoErrorSyncUtc = DateTime.UtcNow;
                }
                CloudLogSyncResult res = await UploadLogsAsync(context, store, "auto_error", localCts.Token).ConfigureAwait(false);
                onCompleted?.Invoke(res);
            } catch {
                // Ignore cancellations / transient offline errors
            }
        });
    }

    static CloudLogSyncResult RecordOutcome(AppSettingsStore store, CloudLogSyncResult res) {
        try {
            if (res.Success) {
                store.CloudLogLastSyncUtc = res.SyncedAtUtc.ToString("O", CultureInfo.InvariantCulture);
                store.CloudLogLastSyncStatus = $"OK ({res.TargetBranch}, {res.BytesUploaded / 1024.0:0.0} KB)";
            } else {
                store.CloudLogLastSyncStatus = $"ERR: {res.Message}";
            }
        } catch {
            // Best-effort prefs update
        }
        return res;
    }

    static async Task<(bool ok, string error)> EnsureBranchExistsAsync(
        string owner, string repo, string branch, string token, CancellationToken ct) {
        string refUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/ref/heads/{Uri.EscapeDataString(branch)}";
        using (HttpRequestMessage checkReq = CreateAuthedRequest(HttpMethod.Get, refUrl, token))
        using (HttpResponseMessage checkResp = await http.SendAsync(checkReq, ct).ConfigureAwait(false)) {
            if (checkResp.IsSuccessStatusCode) {
                return (true, "");
            }
            if (checkResp.StatusCode != HttpStatusCode.NotFound) {
                return (false, FormatHttpFailure(checkResp.StatusCode, $"Failed checking branch '{branch}'"));
            }
        }

        // Branch does not exist yet: resolve default branch HEAD SHA and create branch ref
        string repoUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        string defaultBranch = "master";
        using (HttpRequestMessage repoReq = CreateAuthedRequest(HttpMethod.Get, repoUrl, token))
        using (HttpResponseMessage repoResp = await http.SendAsync(repoReq, ct).ConfigureAwait(false)) {
            if (!repoResp.IsSuccessStatusCode) {
                return (false, FormatHttpFailure(repoResp.StatusCode, "Failed reading repository metadata"));
            }
            string repoJson = await repoResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(repoJson);
            if (doc.RootElement.TryGetProperty("default_branch", out JsonElement dbEl) && !string.IsNullOrWhiteSpace(dbEl.GetString())) {
                defaultBranch = dbEl.GetString();
            }
        }

        string baseRefUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/ref/heads/{Uri.EscapeDataString(defaultBranch)}";
        string baseSha = null;
        using (HttpRequestMessage baseReq = CreateAuthedRequest(HttpMethod.Get, baseRefUrl, token))
        using (HttpResponseMessage baseResp = await http.SendAsync(baseReq, ct).ConfigureAwait(false)) {
            if (!baseResp.IsSuccessStatusCode) {
                return (false, FormatHttpFailure(baseResp.StatusCode, $"Failed resolving base branch '{defaultBranch}'"));
            }
            string baseJson = await baseResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(baseJson);
            if (doc.RootElement.TryGetProperty("object", out JsonElement objEl) &&
                objEl.TryGetProperty("sha", out JsonElement shaEl)) {
                baseSha = shaEl.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(baseSha)) {
            return (false, "Could not resolve base commit SHA to bootstrap diagnostics branch.");
        }

        string createRefUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/refs";
        string payload = JsonSerializer.Serialize(new {
            @ref = $"refs/heads/{branch}",
            sha = baseSha
        });
        using HttpRequestMessage createReq = CreateAuthedRequest(HttpMethod.Post, createRefUrl, token);
        createReq.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage createResp = await http.SendAsync(createReq, ct).ConfigureAwait(false);
        if (createResp.IsSuccessStatusCode || createResp.StatusCode == HttpStatusCode.UnprocessableEntity) {
            return (true, "");
        }
        return (false, FormatHttpFailure(createResp.StatusCode, $"Could not create branch '{branch}'"));
    }

    static async Task<CloudLogSyncResult> PutRepoFileWithRetryAsync(
        string owner,
        string repo,
        string branch,
        string repoPath,
        byte[] contentBytes,
        string commitMessage,
        string token,
        CancellationToken ct) {
        string encodedPath = string.Join("/", repoPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        string baseUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{encodedPath}";
        string b64Content = Convert.ToBase64String(contentBytes);

        for (int attempt = 0; attempt < 2; attempt++) {
            string existingSha = await FetchExistingFileShaAsync(baseUrl, branch, token, ct).ConfigureAwait(false);
            Dictionary<string, object> bodyObj = new() {
                ["message"] = commitMessage,
                ["content"] = b64Content,
                ["branch"] = branch
            };
            if (!string.IsNullOrEmpty(existingSha)) {
                bodyObj["sha"] = existingSha;
            }

            string jsonPayload = JsonSerializer.Serialize(bodyObj);
            using HttpRequestMessage putReq = CreateAuthedRequest(HttpMethod.Put, baseUrl, token);
            putReq.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage putResp = await http.SendAsync(putReq, ct).ConfigureAwait(false);

            if (putResp.IsSuccessStatusCode) {
                string htmlUrl = $"https://github.com/{owner}/{repo}/blob/{branch}/{repoPath}";
                try {
                    string respJson = await putResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(respJson);
                    if (doc.RootElement.TryGetProperty("content", out JsonElement cEl) &&
                        cEl.TryGetProperty("html_url", out JsonElement uEl) &&
                        !string.IsNullOrWhiteSpace(uEl.GetString())) {
                        htmlUrl = uEl.GetString();
                    }
                } catch {
                    // Fallback URL already built
                }

                return new CloudLogSyncResult {
                    Success = true,
                    Message = $"Synced {contentBytes.Length / 1024.0:0.0} KB to {branch}:{repoPath}",
                    HtmlUrl = htmlUrl,
                    TargetBranch = branch,
                    TargetPath = repoPath,
                    BytesUploaded = contentBytes.Length,
                    HttpStatusCode = (int)putResp.StatusCode,
                    SyncedAtUtc = DateTime.UtcNow
                };
            }

            // Retry once on 409 Conflict (remote file updated between GET and PUT)
            if (putResp.StatusCode == HttpStatusCode.Conflict && attempt == 0) {
                continue;
            }

            return new CloudLogSyncResult {
                Success = false,
                TargetBranch = branch,
                TargetPath = repoPath,
                HttpStatusCode = (int)putResp.StatusCode,
                Message = FormatHttpFailure(putResp.StatusCode, $"Upload to {branch}:{repoPath} failed")
            };
        }

        return new CloudLogSyncResult {
            Success = false,
            TargetBranch = branch,
            TargetPath = repoPath,
            Message = "Upload failed after conflict retry."
        };
    }

    static async Task<string> FetchExistingFileShaAsync(string baseUrl, string branch, string token, CancellationToken ct) {
        string url = $"{baseUrl}?ref={Uri.EscapeDataString(branch)}";
        using HttpRequestMessage req = CreateAuthedRequest(HttpMethod.Get, url, token);
        using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) {
            return null;
        }
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sha", out JsonElement shaEl)) {
                return shaEl.GetString();
            }
        } catch {
            // Treat as new file
        }
        return null;
    }

    static HttpRequestMessage CreateAuthedRequest(HttpMethod method, string url, string token) {
        HttpRequestMessage req = new(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    static string FormatHttpFailure(HttpStatusCode status, string prefix) {
        return status switch {
            HttpStatusCode.Unauthorized => $"{prefix}: HTTP 401 (Invalid or expired GitHub token).",
            HttpStatusCode.Forbidden => $"{prefix}: HTTP 403 (Token lacks Contents: Read & Write scope).",
            HttpStatusCode.NotFound => $"{prefix}: HTTP 404 (Repository or branch not found).",
            HttpStatusCode.Conflict => $"{prefix}: HTTP 409 (Remote conflict, please retry).",
            HttpStatusCode.UnprocessableEntity => $"{prefix}: HTTP 422 (Validation/branch protection rejected commit).",
            _ => $"{prefix}: HTTP {(int)status} ({status})."
        };
    }

    static string LoadToken(AppSettingsStore store) {
        string stored = store?.CloudLogEncryptedToken;
        if (string.IsNullOrWhiteSpace(stored)) {
            return "";
        }
        return DecryptTokenWithKeystore(stored);
    }

    static IKey GetOrCreateKeystoreSecretKey() {
        KeyStore ks = KeyStore.GetInstance("AndroidKeyStore");
        ks.Load(null);
        if (ks.ContainsAlias(KeyAlias)) {
            IKey existing = ks.GetKey(KeyAlias, null);
            if (existing != null) {
                return existing;
            }
        }
        KeyGenerator kg = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
        KeyGenParameterSpec spec = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();
        kg.Init(spec);
        return kg.GenerateKey();
    }

    static string EncryptTokenWithKeystore(string plainToken) {
        try {
            IKey key = GetOrCreateKeystoreSecretKey();
            Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
            byte[] iv = cipher.GetIV();
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plainToken);
            byte[] cipherBytes = cipher.DoFinal(plainBytes);
            return $"{KeystorePrefix}{Convert.ToBase64String(iv)}:{Convert.ToBase64String(cipherBytes)}";
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Android Keystore encryption unavailable ({ex.GetType().Name})");
            return "";
        }
    }

    static string DecryptTokenWithKeystore(string stored) {
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
            IKey key = GetOrCreateKeystoreSecretKey();
            Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
            byte[] plainBytes = cipher.DoFinal(cipherBytes);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Android Keystore decryption failed ({ex.GetType().Name}); re-enter token in Cloud Setup.");
            return "";
        }
    }
}
