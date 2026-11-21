using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Android.Content;

using Cavern.Filters;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Manages the official offline-bundled &amp; 8,850-model searchable <c>jaakkopasanen/AutoEq</c> (MIT) headphone database,
/// persistent local cache for on-demand downloads, custom Equalizer APO / Wavelet imports, smart Bluetooth/USB
/// device-name matching, and Slot 6A wiring with automatic Zero Double-EQ Guard.
/// </summary>
public static class AutoEqService {
    public const string CustomProfileId = "custom_autoeq";

    const string BundledAssetPath = "autoeq/bundled_profiles.aeqz";
    const string CatalogAssetPath = "autoeq/catalog_index.aeqz";
    const string LicenseAssetPath = "autoeq/LICENSES.txt";
    const string AutoEqRawBaseUrl = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";

    static readonly object loadLock = new();
    static readonly HttpClient httpClient = CreateHttpClient();
    static Dictionary<string, AutoEqHeadphoneProfile> bundledById;
    static List<AutoEqHeadphoneProfile> bundledList;
    static List<AutoEqCatalogEntry> catalogEntries;
    static Dictionary<string, AutoEqCatalogEntry> catalogById;
    static bool assetsLoaded;

    static readonly HashSet<string> GenericDeviceStopWords = new(StringComparer.OrdinalIgnoreCase) {
        "audio", "bluetooth", "bt", "device", "earbuds", "earphone", "earphones",
        "headphone", "headphones", "headset", "hands", "free", "handsfree", "le",
        "phone", "speaker", "speakers", "stereo", "usb", "usbc", "wired", "wireless",
        "adapter", "dongle", "dac", "amp", "android", "samsung", "pixel", "builtin", "my"
    };

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CavernizeAndroid-AutoEq/3.0");
        return client;
    }

    /// <summary>
    /// Ensures both the offline bundled profiles (127 popular models) and the full 8,850-model AutoEq catalog index
    /// are loaded into memory. Safe to call repeatedly from any thread.
    /// </summary>
    public static void EnsureLoaded(Context ctx) {
        if (assetsLoaded || ctx == null) {
            return;
        }
        lock (loadLock) {
            if (assetsLoaded) {
                return;
            }
            bundledById = new Dictionary<string, AutoEqHeadphoneProfile>(StringComparer.OrdinalIgnoreCase);
            bundledList = [];
            catalogEntries = [];
            catalogById = new Dictionary<string, AutoEqCatalogEntry>(StringComparer.OrdinalIgnoreCase);

            LoadBundledProfilesLocked(ctx);
            LoadCatalogIndexLocked(ctx);
            assetsLoaded = true;
            AppLogger.Info($"AutoEq database ready: {bundledList.Count} offline bundled profiles, {catalogEntries.Count} total catalog models.");
        }
    }

    /// <summary>
    /// Total number of searchable official AutoEq headphone/IEM models in the embedded catalog index.
    /// </summary>
    public static int GetTotalCatalogCount(Context ctx) {
        EnsureLoaded(ctx);
        return catalogEntries?.Count ?? 0;
    }

    /// <summary>
    /// Returns all bundled offline popular headphone/IEM profiles sorted alphabetically.
    /// </summary>
    public static IReadOnlyList<AutoEqHeadphoneProfile> GetBundledProfiles(Context ctx) {
        EnsureLoaded(ctx);
        return bundledList ?? (IReadOnlyList<AutoEqHeadphoneProfile>)Array.Empty<AutoEqHeadphoneProfile>();
    }

    /// <summary>
    /// Searches all 8,850+ official AutoEq models using multi-token fuzzy matching, preferring bundled offline
    /// profiles and top laboratory measurement rigs (oratory1990 &gt; B&amp;K 5128/GRAS &gt; 711 &gt; Rtings).
    /// </summary>
    public static List<AutoEqCatalogEntry> SearchCatalog(Context ctx, string query, int maxResults = 60) {
        EnsureLoaded(ctx);
        if (catalogEntries == null || catalogEntries.Count == 0) {
            return [];
        }

        string trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0) {
            // Empty query returns the curated offline bundled favorites sorted by name
            return catalogEntries
                .Where(e => e.IsBundledOffline)
                .OrderByDescending(e => e.SourceRank)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList();
        }

        string normalizedQuery = NormalizeSearchText(trimmed);
        string[] queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.Length == 0) {
            return [];
        }

        List<(AutoEqCatalogEntry Entry, int Score)> scored = new(128);
        for (int i = 0; i < catalogEntries.Count; i++) {
            AutoEqCatalogEntry entry = catalogEntries[i];
            string normName = NormalizeSearchText(entry.Name);
            string normRig = NormalizeSearchText(entry.MeasurementRig);

            bool allTokensFound = true;
            int tokenHits = 0;
            for (int t = 0; t < queryTokens.Length; t++) {
                string tok = queryTokens[t];
                if (normName.Contains(tok, StringComparison.Ordinal)) {
                    tokenHits++;
                } else if (normRig.Contains(tok, StringComparison.Ordinal)) {
                    tokenHits++;
                } else {
                    allTokensFound = false;
                    break;
                }
            }
            if (!allTokensFound) {
                continue;
            }

            int score = entry.SourceRank;
            if (entry.IsBundledOffline) {
                score += 65;
            }
            if (normName.Equals(normalizedQuery, StringComparison.Ordinal)) {
                score += 500;
            } else if (normName.StartsWith(normalizedQuery, StringComparison.Ordinal)) {
                score += 240;
            } else if (normName.Contains(normalizedQuery, StringComparison.Ordinal)) {
                score += 140;
            }
            score += tokenHits * 25;
            // Favor concise primary model names over specialized pad/ANC variants when equal
            score -= Math.Min(45, normName.Length / 2);

            scored.Add((entry, score));
        }

        scored.Sort((a, b) => {
            int cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
        });

        int take = Math.Min(maxResults, scored.Count);
        List<AutoEqCatalogEntry> results = new(take);
        for (int i = 0; i < take; i++) {
            results.Add(scored[i].Entry);
        }
        return results;
    }

    /// <summary>
    /// Attempts high-confidence automatic matching of an Android Bluetooth/USB <c>AudioDeviceInfo.ProductName</c>
    /// (e.g. "Galaxy Buds2 Pro (A4B1)", "WH-1000XM5", "AirPods Pro", "HD 650") to an official AutoEq catalog entry.
    /// Returns null for generic device labels ("Bluetooth", "USB-C DAC", "Speaker") to prevent false matches.
    /// </summary>
    public static AutoEqCatalogEntry MatchDeviceName(Context ctx, string productName) {
        EnsureLoaded(ctx);
        if (string.IsNullOrWhiteSpace(productName) || catalogEntries == null || catalogEntries.Count == 0) {
            return null;
        }

        string cleaned = StripDeviceNoise(productName);
        string normDev = NormalizeSearchText(cleaned);
        if (normDev.Length < 3) {
            return null;
        }

        string[] devTokens = normDev
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !GenericDeviceStopWords.Contains(t))
            .ToArray();

        if (devTokens.Length == 0) {
            return null;
        }

        // Require at least one distinctive model token (>= 2 chars or containing a digit)
        bool hasDistinctive = false;
        for (int i = 0; i < devTokens.Length; i++) {
            if (devTokens[i].Length >= 3 || devTokens[i].Any(char.IsDigit)) {
                hasDistinctive = true;
                break;
            }
        }
        if (!hasDistinctive) {
            return null;
        }

        AutoEqCatalogEntry bestEntry = null;
        int bestScore = 0;

        for (int i = 0; i < catalogEntries.Count; i++) {
            AutoEqCatalogEntry entry = catalogEntries[i];
            string normModel = NormalizeSearchText(entry.Name);
            string[] modelTokens = normModel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (modelTokens.Length == 0) {
                continue;
            }

            // Exact normalized match or exact model suffix (e.g. device="Galaxy Buds2 Pro" vs model="Samsung Galaxy Buds2 Pro")
            int score = 0;
            if (normModel.Equals(normDev, StringComparison.Ordinal)) {
                score = 1000;
            } else if (normModel.EndsWith(" " + normDev, StringComparison.Ordinal) ||
                       normDev.EndsWith(" " + normModel, StringComparison.Ordinal)) {
                score = 860;
            } else {
                // Token Jaccard / coverage matching (exact tokens only, preventing "Buds2" matching "Buds2 Pro" over "Buds2")
                int matchedDev = 0;
                for (int d = 0; d < devTokens.Length; d++) {
                    for (int m = 0; m < modelTokens.Length; m++) {
                        if (devTokens[d].Equals(modelTokens[m], StringComparison.Ordinal)) {
                            matchedDev++;
                            break;
                        }
                    }
                }
                if (matchedDev == 0) {
                    continue;
                }
                double devCoverage = matchedDev / (double)devTokens.Length;
                int nonBrandModelTokens = Math.Max(1, modelTokens.Length - 1);
                double modelCoverage = matchedDev / (double)nonBrandModelTokens;

                // Require high coverage on BOTH sides so "Galaxy Buds2" does not falsely match "Galaxy Buds2 Pro"
                if (devCoverage >= 0.99 && modelCoverage >= 0.75) {
                    score = (int)(650 + modelCoverage * 150);
                    // Penalize extra unmatched suffix tokens (e.g. "pro", "anc", "2", "ii")
                    int extraTokens = Math.Abs(modelTokens.Length - devTokens.Length);
                    score -= extraTokens * 35;
                } else {
                    continue;
                }
            }

            score += entry.SourceRank;
            if (entry.IsBundledOffline) {
                score += 60;
            }

            if (score > bestScore) {
                bestScore = score;
                bestEntry = entry;
            }
        }

        return bestScore >= 720 ? bestEntry : null;
    }

    /// <summary>
    /// Synchronously resolves a profile if it is bundled offline, custom local, or already cached on disk.
    /// Returns null if an online download is required (call <see cref="ResolveProfileAsync"/> in that case).
    /// </summary>
    public static AutoEqHeadphoneProfile TryResolveOffline(Context ctx, AppSettingsStore store, string profileId) {
        if (string.IsNullOrWhiteSpace(profileId)) {
            return null;
        }
        EnsureLoaded(ctx);

        if (string.Equals(profileId, CustomProfileId, StringComparison.OrdinalIgnoreCase)) {
            if (store != null && !string.IsNullOrEmpty(store.AutoEqCustomPath) && File.Exists(store.AutoEqCustomPath)) {
                return ParseCustomFile(store.AutoEqCustomPath);
            }
            return null;
        }

        if (bundledById != null && bundledById.TryGetValue(profileId, out AutoEqHeadphoneProfile bundled)) {
            return bundled;
        }

        // Check persistent local cache for previously downloaded AutoEq catalog profiles
        string cacheFile = GetCacheFilePath(ctx, profileId);
        if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile) &&
            catalogById != null && catalogById.TryGetValue(profileId, out AutoEqCatalogEntry entry)) {
            try {
                string txt = File.ReadAllText(cacheFile, Encoding.UTF8);
                return BuildProfileFromText(entry, txt, isBundled: false);
            } catch (Exception ex) {
                AppLogger.Warn($"Failed reading cached AutoEq '{profileId}': {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves any of the 8,850+ AutoEq profiles: returns instantly if bundled offline or cached,
    /// otherwise fetches the official <c>ParametricEQ.txt</c> from <c>jaakkopasanen/AutoEq</c> and caches it locally.
    /// </summary>
    public static async Task<AutoEqHeadphoneProfile> ResolveProfileAsync(
        Context ctx,
        AppSettingsStore store,
        string profileId,
        CancellationToken ct = default) {
        AutoEqHeadphoneProfile offline = TryResolveOffline(ctx, store, profileId);
        if (offline != null) {
            return offline;
        }

        EnsureLoaded(ctx);
        if (catalogById == null || !catalogById.TryGetValue(profileId, out AutoEqCatalogEntry entry)) {
            return null;
        }

        string decodedRel = Uri.UnescapeDataString(entry.RemoteRelativePath);
        int slash = decodedRel.LastIndexOf('/');
        string leafName = slash >= 0 ? decodedRel.Substring(slash + 1) : decodedRel;
        string url = AutoEqRawBaseUrl + entry.RemoteRelativePath + "/" + Uri.EscapeDataString(leafName + " ParametricEQ.txt");

        using HttpResponseMessage resp = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        AutoEqHeadphoneProfile profile = BuildProfileFromText(entry, txt, isBundled: false);
        if (profile == null || profile.Bands.Length == 0) {
            throw new InvalidDataException($"No parametric EQ bands parsed for '{entry.Name}'.");
        }

        try {
            string cachePath = GetCacheFilePath(ctx, profileId);
            if (!string.IsNullOrEmpty(cachePath)) {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                await File.WriteAllTextAsync(cachePath, txt, Encoding.UTF8, ct).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            AppLogger.Warn($"Could not cache AutoEq file for '{profileId}': {ex.Message}");
        }

        return profile;
    }

    /// <summary>
    /// Imports a user-selected Equalizer APO, AutoEq, oratory1990, or Wavelet <c>.txt</c> file into private storage.
    /// </summary>
    public static bool ImportCustomEqFile(
        Context ctx,
        AppSettingsStore store,
        string sourcePath,
        out AutoEqHeadphoneProfile profile,
        out string errorMessage) {
        profile = null;
        errorMessage = null;
        try {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) {
                errorMessage = AndroidStrings.Active["AutoEqFileNotFound"];
                return false;
            }
            string text = File.ReadAllText(sourcePath, Encoding.UTF8);
            ParsedParametricEq parsed = ParametricEqualizer.ParseEqualizerApo(text);
            if (parsed.Bands == null || parsed.Bands.Length == 0) {
                errorMessage = AndroidStrings.Active["AutoEqNoValidFilters"];
                return false;
            }

            string customDir = Path.Combine(ctx.FilesDir.AbsolutePath, "custom_autoeq");
            Directory.CreateDirectory(customDir);
            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) {
                fileName = "custom_autoeq.txt";
            }
            string destPath = Path.Combine(customDir, fileName);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase)) {
                File.Copy(sourcePath, destPath, overwrite: true);
            }

            store.AutoEqCustomPath = destPath;
            store.AutoEqProfileId = CustomProfileId;
            store.AutoEqProfileName = Path.GetFileNameWithoutExtension(fileName);
            store.AutoEqEnabled = true;

            profile = ParseCustomFile(destPath);
            AppLogger.Info($"Imported custom AutoEq/APO file '{destPath}' ({profile.Bands.Length} bands, preamp={profile.ExplicitPreampDb:0.0} dB).");
            return true;
        } catch (Exception ex) {
            AppLogger.Error("AutoEqService.ImportCustomEqFile", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Applies the saved AutoEq profile, neutral target curve, and automatic Zero Double-EQ Guard to
    /// <see cref="RealtimePlayerEngine.AutoEqStage"/> and mirrors state into Slot 3 (<see cref="SpatialSceneHub"/>).
    /// </summary>
    public static void ApplySavedToEngine(Context ctx, AppSettingsStore store, RealtimePlayerEngine engine) {
        if (ctx == null || store == null || engine == null) {
            return;
        }
        AutoEqHeadphoneProfile profile = store.AutoEqEnabled
            ? TryResolveOffline(ctx, store, store.AutoEqProfileId)
            : null;
        ApplyProfileToEngine(store, engine, profile);
    }

    /// <summary>
    /// Applies a resolved <see cref="AutoEqHeadphoneProfile"/> (or null to bypass) to Slot 6A with exact
    /// Zero Double-EQ Guard synchronization against the active binaural HRIR profile.
    /// </summary>
    public static void ApplyProfileToEngine(AppSettingsStore store, RealtimePlayerEngine engine, AutoEqHeadphoneProfile profile) {
        if (store == null || engine == null) {
            return;
        }

        BinauralCompensationMode compMode = BinauralProfileService.GetActiveCompensationMode(store);
        AutoEqNeutralTarget target = (AutoEqNeutralTarget)Math.Clamp(
            store.AutoEqTargetIndex,
            (int)AutoEqNeutralTarget.DiffuseFieldNeutral,
            (int)AutoEqNeutralTarget.NativeAutoEqProfile);

        bool shouldEnable = store.AutoEqEnabled && profile != null && profile.Bands != null && profile.Bands.Length > 0;
        engine.RequestSmoothTransition();
        if (shouldEnable) {
            engine.AutoEqStage.ApplyConfiguration(profile, target, compMode, store.AutoEqDoubleEqGuard);
            engine.AutoEqStage.UserEnabled = true;
            engine.SceneHub.ActiveAutoEqProfileId = profile.Id;
            engine.SceneHub.ActiveAutoEqProfileName = profile.Name;
            AppLogger.Info(
                $"Slot 6A AutoEq active: '{profile.Name}' (target={target}, hrirMode={compMode}, " +
                $"guard={engine.AutoEqStage.DoubleEqGuardActive}, sections={engine.AutoEqStage.ActiveSectionCount}, " +
                $"preamp={engine.AutoEqStage.EffectivePreampDb:0.00} dB)");
        } else {
            engine.AutoEqStage.UserEnabled = false;
            engine.AutoEqStage.ClearProfile();
            engine.SceneHub.ActiveAutoEqProfileId = string.Empty;
            engine.SceneHub.ActiveAutoEqProfileName = string.Empty;
        }
        engine.SceneHub.ActiveBinauralCompensationMode = compMode;
    }

    /// <summary>
    /// Immediately updates the Zero Double-EQ Guard in Slot 6A whenever the user switches Binaural HRIR / BRIR / Direct 2.0.
    /// </summary>
    public static void SyncBinauralModeToEngine(AppSettingsStore store, RealtimePlayerEngine engine) {
        if (store == null || engine == null) {
            return;
        }
        BinauralCompensationMode compMode = BinauralProfileService.GetActiveCompensationMode(store);
        engine.SceneHub.ActiveBinauralCompensationMode = compMode;
        engine.AutoEqStage.UpdateBinauralMode(compMode);
    }

    /// <summary>
    /// Reads official MIT license attribution for <c>jaakkopasanen/AutoEq</c> from bundled assets.
    /// </summary>
    public static string ReadLicenseCredits(Context ctx) {
        try {
            using Stream s = ctx.Assets.Open(LicenseAssetPath);
            using StreamReader sr = new(s, Encoding.UTF8);
            return sr.ReadToEnd();
        } catch {
            return "AutoEq Headphone Equalization Database — Jaakko Pasanen (MIT License)\n" +
                   "Measurement Contributors: oratory1990, crinacle, Rtings, Innerfidelity, Super Review.";
        }
    }

    static void LoadBundledProfilesLocked(Context ctx) {
        try {
            using Stream raw = ctx.Assets.Open(BundledAssetPath);
            using GZipStream gz = new(raw, CompressionMode.Decompress);
            using JsonDocument doc = JsonDocument.Parse(gz);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) {
                return;
            }
            foreach (JsonElement item in doc.RootElement.EnumerateArray()) {
                string id = item.GetProperty("id").GetString() ?? string.Empty;
                string name = item.GetProperty("name").GetString() ?? string.Empty;
                string rig = item.GetProperty("rig").GetString() ?? string.Empty;
                string ffCode = item.GetProperty("ff").GetString() ?? "OE";
                double preamp = item.GetProperty("preamp").GetDouble();

                List<ParametricBand> bands = [];
                foreach (JsonElement b in item.GetProperty("bands").EnumerateArray()) {
                    string ft = b[0].GetString() ?? "PK";
                    double fc = b[1].GetDouble();
                    double gn = b[2].GetDouble();
                    double q = b[3].GetDouble();
                    bands.Add(new ParametricBand(ParseFilterType(ft), fc, gn, q, true));
                }

                HeadphoneFormFactor ff = ParseFormFactor(ffCode);
                AutoEqHeadphoneProfile prof = new() {
                    Id = id,
                    Name = name,
                    MeasurementRig = rig,
                    FormFactor = ff,
                    SourceTarget = ff == HeadphoneFormFactor.InEar || ff == HeadphoneFormFactor.Earbud
                        ? AutoEqSourceTarget.AutoEqInEar
                        : AutoEqSourceTarget.HarmanOverEar2018,
                    ExplicitPreampDb = preamp,
                    HasExplicitPreamp = true,
                    Bands = bands.ToArray(),
                    IsBundledOffline = true
                };
                bundledById[id] = prof;
                bundledList.Add(prof);
            }
        } catch (Exception ex) {
            AppLogger.Error("AutoEqService.LoadBundledProfiles", ex);
        }
    }

    static void LoadCatalogIndexLocked(Context ctx) {
        try {
            using Stream raw = ctx.Assets.Open(CatalogAssetPath);
            using GZipStream gz = new(raw, CompressionMode.Decompress);
            using StreamReader sr = new(gz, Encoding.UTF8);
            string line;
            while ((line = sr.ReadLine()) != null) {
                if (line.Length == 0) {
                    continue;
                }
                string[] cols = line.Split('\t');
                if (cols.Length < 7) {
                    continue;
                }
                AutoEqCatalogEntry entry = new() {
                    Id = cols[0],
                    Name = cols[1],
                    MeasurementRig = cols[2],
                    FormFactor = ParseFormFactor(cols[3]),
                    SourceRank = int.TryParse(cols[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 50,
                    IsBundledOffline = cols[5] == "1",
                    RemoteRelativePath = cols[6]
                };
                catalogEntries.Add(entry);
                if (!catalogById.ContainsKey(entry.Id)) {
                    catalogById[entry.Id] = entry;
                }
            }
        } catch (Exception ex) {
            AppLogger.Error("AutoEqService.LoadCatalogIndex", ex);
        }
    }

    static AutoEqHeadphoneProfile BuildProfileFromText(AutoEqCatalogEntry entry, string txt, bool isBundled) {
        ParsedParametricEq parsed = ParametricEqualizer.ParseEqualizerApo(txt);
        if (parsed.Bands == null || parsed.Bands.Length == 0) {
            return null;
        }
        return new AutoEqHeadphoneProfile {
            Id = entry.Id,
            Name = entry.Name,
            MeasurementRig = entry.MeasurementRig,
            FormFactor = entry.FormFactor,
            SourceTarget = entry.InferSourceTarget(),
            ExplicitPreampDb = parsed.PreampDb,
            HasExplicitPreamp = parsed.HasExplicitPreamp,
            Bands = parsed.Bands,
            IsBundledOffline = isBundled
        };
    }

    static AutoEqHeadphoneProfile ParseCustomFile(string path) {
        string txt = File.ReadAllText(path, Encoding.UTF8);
        ParsedParametricEq parsed = ParametricEqualizer.ParseEqualizerApo(txt);
        return new AutoEqHeadphoneProfile {
            Id = CustomProfileId,
            Name = Path.GetFileNameWithoutExtension(path),
            MeasurementRig = "Custom Equalizer APO / AutoEq",
            FormFactor = HeadphoneFormFactor.OverEar,
            SourceTarget = AutoEqSourceTarget.AlreadyNeutral,
            ExplicitPreampDb = parsed.PreampDb,
            HasExplicitPreamp = parsed.HasExplicitPreamp,
            Bands = parsed.Bands,
            IsBundledOffline = true,
            CustomFilePath = path
        };
    }

    static string GetCacheFilePath(Context ctx, string profileId) {
        if (ctx?.FilesDir == null || string.IsNullOrWhiteSpace(profileId)) {
            return string.Empty;
        }
        StringBuilder safe = new(profileId.Length);
        for (int i = 0; i < profileId.Length; i++) {
            char c = profileId[i];
            safe.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }
        return Path.Combine(ctx.FilesDir.AbsolutePath, "autoeq_cache", safe + ".txt");
    }

    static ParametricFilterType ParseFilterType(string code) => code switch {
        "LS" or "LSC" => ParametricFilterType.LowShelf,
        "HS" or "HSC" => ParametricFilterType.HighShelf,
        "LP" or "LPQ" => ParametricFilterType.LowPass,
        "HP" or "HPQ" => ParametricFilterType.HighPass,
        "NO" => ParametricFilterType.Notch,
        "BP" => ParametricFilterType.BandPass,
        "AP" => ParametricFilterType.AllPass,
        _ => ParametricFilterType.Peaking
    };

    static HeadphoneFormFactor ParseFormFactor(string code) => code switch {
        "IE" => HeadphoneFormFactor.InEar,
        "ON" => HeadphoneFormFactor.OnEar,
        "EB" => HeadphoneFormFactor.Earbud,
        _ => HeadphoneFormFactor.OverEar
    };

    static string StripDeviceNoise(string raw) {
        // Remove trailing parenthetical MAC/ID tags like "(A4B1)" or "LE-" prefixes and possessive "'s " prefixes
        string s = raw.Trim();
        int apos = s.IndexOf("'s ", StringComparison.OrdinalIgnoreCase);
        if (apos > 0 && apos < s.Length - 3) {
            s = s.Substring(apos + 3);
        }
        if (s.StartsWith("LE-", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("LE_", StringComparison.OrdinalIgnoreCase)) {
            s = s.Substring(3);
        }
        int paren = s.LastIndexOf('(');
        if (paren > 2 && s.EndsWith(')')) {
            string inside = s.Substring(paren + 1, s.Length - paren - 2).Trim();
            // Strip short hex/suffix tags like "(A4B1)" while keeping meaningful names like "(2)"
            if (inside.Length >= 4 && inside.All(c => char.IsLetterOrDigit(c) || c == ':')) {
                s = s.Substring(0, paren).Trim();
            }
        }
        return s;
    }

    static string NormalizeSearchText(string input) {
        if (string.IsNullOrEmpty(input)) {
            return string.Empty;
        }
        StringBuilder sb = new(input.Length);
        bool prevSpace = true;
        for (int i = 0; i < input.Length; i++) {
            char c = char.ToLowerInvariant(input[i]);
            if (char.IsLetterOrDigit(c)) {
                sb.Append(c);
                prevSpace = false;
            } else if (!prevSpace) {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }
}
