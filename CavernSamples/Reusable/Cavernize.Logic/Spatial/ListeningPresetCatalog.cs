using System.Globalization;
using System.Text.Json;

namespace Cavernize.Logic.Spatial;

/// <summary>Curated one-tap spatial listening personality presets mapping directly to Cavern's DSP chain.</summary>
public enum ListeningPresetKind {
    /// <summary>Transparent anechoic binaural rendering without artificial room or EQ coloration.</summary>
    Natural = 0,
    /// <summary>7.1.4 Cinema spatial stage with Atmos-aware upmixing, ISM stage reflections, and Hybrid Neural+DirAC.</summary>
    Cinema3D = 1,
    /// <summary>ITU-R BS.1116 mastering reference room (-23 LUFS EBU R128, neutral room reflections, Filter Studio).</summary>
    StudioReference = 2,
    /// <summary>Interactive Spatial Karaoke stage separating lead/backing vocals with streaming loudness leveling.</summary>
    Karaoke = 3,
    /// <summary>User-customized DSP configuration outside the curated one-tap presets.</summary>
    Custom = 4
}

/// <summary>Declarative specification of a curated spatial listening preset in <see cref="ListeningPresetCatalog"/>.</summary>
public sealed class ListeningPresetSpec {
    /// <summary>Canonical enum kind for this preset.</summary>
    public ListeningPresetKind Kind { get; init; }
    /// <summary>Stable lowercase preset identifier persisted in <c>activeListeningPresetId</c>.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Human-readable preset display name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Concise audiophile description of the DSP routing applied by this preset.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Enables binaural headphone HRTF virtualizer.</summary>
    public bool HeadphoneVirtualizer { get; init; }
    /// <summary>Enables classic matrix surround upmixing.</summary>
    public bool MatrixUpmixing { get; init; }
    /// <summary>Enables Cavernize 3D parametric DirAC spatial upmixing.</summary>
    public bool CavernizeUpmix { get; init; }
    /// <summary>Preserves native Dolby Atmos / object beds when upmixing.</summary>
    public bool AtmosAwareUpmix { get; init; }
    /// <summary>Enables physical shoebox Image-Source Model (ISM) early reflections.</summary>
    public bool IsmRoomEnabled { get; init; }
    /// <summary>Index into <see cref="Cavern.Virtualizer.IsmRoomPreset"/> (0=MasteringStudio..4=CustomEnvironment).</summary>
    public int IsmRoomPresetIndex { get; init; }
    /// <summary>Index into <see cref="LoudnessNormalizationMode"/> (0=Off..4=DolbyDialnorm).</summary>
    public int LoudnessNormModeIndex { get; init; }
    /// <summary>Enables ISO 226:2023 equal-loudness contour compensation.</summary>
    public bool Iso226Enabled { get; init; }
    /// <summary>Enables interactive Spatial Karaoke vocal attenuation/staging.</summary>
    public bool SpatialKaraokeEnabled { get; init; }
    /// <summary>Index into <see cref="NeuralSpatialUpmixMode"/> (0=Off, 1=Stems3DObjects, 2=HybridNeuralDirAc).</summary>
    public int NeuralSpatialUpmixModeIndex { get; init; }
    /// <summary>Enables loudspeaker transaural crosstalk cancellation (auto-bypassed on headphones).</summary>
    public bool TransauralXtcEnabled { get; init; }
    /// <summary>Enables parametric Filter Studio DSP correction.</summary>
    public bool FilterStudioEnabled { get; init; }
}

/// <summary>Curated workspace shell layout presets controlling startup tab and Pro-mode control density.</summary>
public enum WorkspacePresetKind {
    /// <summary>Clean everyday music &amp; spatial playback workspace starting on Library/Player.</summary>
    Listener = 0,
    /// <summary>Vocal-first live sing-along &amp; lyrics workspace starting on Library/Player.</summary>
    Karaoke = 1,
    /// <summary>Full mastering engineer &amp; neural exporter workspace starting on Studio with Pro diagnostics.</summary>
    StudioPro = 2
}

/// <summary>Declarative specification of a curated workspace layout preset in <see cref="ListeningPresetCatalog"/>.</summary>
public sealed class WorkspacePresetSpec {
    /// <summary>Canonical workspace enum kind.</summary>
    public WorkspacePresetKind Kind { get; init; }
    /// <summary>Stable lowercase identifier persisted in <c>activeWorkspacePresetId</c>.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Human-readable workspace display name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Short summary of tab routing and diagnostic visibility.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Initial 3-tab shell index (0=Library/Player, 1=Sound, 2=Export/Studio).</summary>
    public int DefaultTab { get; init; }
    /// <summary>Whether Pro engineering controls should be expanded by default.</summary>
    public bool ProModeDefault { get; init; }
    /// <summary>Whether extended acoustic/neural diagnostics should be surfaced on the Sound hub.</summary>
    public bool ShowSoundDiagnostics { get; init; }
}

/// <summary>Versioned, secret-free portable preferences snapshot for cross-device backup and restore.</summary>
public sealed class PortableSettingsSnapshot {
    /// <summary>Current supported portable JSON schema version.</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>Schema version number (always 1 for v1 snapshots).</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    /// <summary>ISO-8601 UTC export timestamp.</summary>
    public string ExportedUtc { get; init; } = string.Empty;
    /// <summary>Originating application name (<c>"Cavernize"</c>).</summary>
    public string AppName { get; init; } = "Cavernize";
    /// <summary>Active listening preset identifier at export time.</summary>
    public string ActiveListeningPresetId { get; init; } = "custom";
    /// <summary>Active workspace preset identifier at export time.</summary>
    public string ActiveWorkspacePresetId { get; init; } = "listener";
    /// <summary>Count of sensitive/secret keys stripped or rejected during serialization/deserialization.</summary>
    public int StrippedSecretCount { get; init; }
    /// <summary>Sanitized non-secret preference entries ready for portable export or import.</summary>
    public Dictionary<string, object> Preferences { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Registry of curated Listening &amp; Workspace presets plus zero-secret portable JSON settings import/export.</summary>
public static class ListeningPresetCatalog {
    static readonly HashSet<string> SafeConfigKeys = new(StringComparer.OrdinalIgnoreCase) {
        "binauralProfile", "binauralProfileId", "activeListeningPresetId", "activeWorkspacePresetId",
        "autoEqProfileId", "mvsepPresetId", "neuralQuantizationModeIdx",
        "truehddLicenceAccepted", "transauralXtcAutoBypassHeadphones", "sourceTrackKey"
    };

    static readonly HashSet<string> ExactSecretKeys = new(StringComparer.OrdinalIgnoreCase) {
        "mvsepApiToken", "cloudLogToken", "cloudLogEncToken", "cloudLogEncryptedToken",
        "liveBridgeEncPairSecret", "liveBridgeEncryptedPairSecret", "liveBridgePairSecret",
        "apiKey", "secret", "token", "password", "auth", "key", "credential"
    };

    static readonly string[] SecretSubstrings = [
        "secret", "password", "token", "apikey", "api_key", "privatekey",
        "private_key", "passphrase", "credential", "enctoken", "encpair"
    ];

    /// <summary>Curated spatial listening presets covering neutral binaural, 3D cinema, studio reference, karaoke, and custom.</summary>
    public static IReadOnlyList<ListeningPresetSpec> ListeningPresets { get; } = [
        new ListeningPresetSpec {
            Kind = ListeningPresetKind.Natural, Id = "natural", Name = "Natural Pure",
            Description = "Transparent binaural HRTF without room reflections, upmix coloration, or loudness scaling.",
            HeadphoneVirtualizer = true, MatrixUpmixing = false, CavernizeUpmix = false, AtmosAwareUpmix = true,
            IsmRoomEnabled = false, IsmRoomPresetIndex = 0, LoudnessNormModeIndex = 0, Iso226Enabled = false,
            SpatialKaraokeEnabled = false, NeuralSpatialUpmixModeIndex = 0, TransauralXtcEnabled = false, FilterStudioEnabled = false
        },
        new ListeningPresetSpec {
            Kind = ListeningPresetKind.Cinema3D, Id = "cinema_3d", Name = "Cinema 3D",
            Description = "Atmos-aware 7.1.4 spatial upmix with Hybrid Neural+DirAC, Cinema ISM stage, and ISO 226 contour.",
            HeadphoneVirtualizer = true, MatrixUpmixing = false, CavernizeUpmix = true, AtmosAwareUpmix = true,
            IsmRoomEnabled = true, IsmRoomPresetIndex = 3, LoudnessNormModeIndex = 2, Iso226Enabled = true,
            SpatialKaraokeEnabled = false, NeuralSpatialUpmixModeIndex = 2, TransauralXtcEnabled = false, FilterStudioEnabled = false
        },
        new ListeningPresetSpec {
            Kind = ListeningPresetKind.StudioReference, Id = "studio_reference", Name = "Studio Reference",
            Description = "ITU-R BS.1116 mastering room with EBU R128 (-23 LUFS) loudness anchor and Filter Studio EQ.",
            HeadphoneVirtualizer = true, MatrixUpmixing = false, CavernizeUpmix = false, AtmosAwareUpmix = true,
            IsmRoomEnabled = true, IsmRoomPresetIndex = 0, LoudnessNormModeIndex = 1, Iso226Enabled = false,
            SpatialKaraokeEnabled = false, NeuralSpatialUpmixModeIndex = 0, TransauralXtcEnabled = false, FilterStudioEnabled = true
        },
        new ListeningPresetSpec {
            Kind = ListeningPresetKind.Karaoke, Id = "karaoke", Name = "Spatial Karaoke",
            Description = "3D stem staging with live lead-vocal attenuation and streaming loudness leveling (-14 LUFS).",
            HeadphoneVirtualizer = true, MatrixUpmixing = false, CavernizeUpmix = false, AtmosAwareUpmix = true,
            IsmRoomEnabled = false, IsmRoomPresetIndex = 1, LoudnessNormModeIndex = 3, Iso226Enabled = false,
            SpatialKaraokeEnabled = true, NeuralSpatialUpmixModeIndex = 1, TransauralXtcEnabled = false, FilterStudioEnabled = false
        },
        new ListeningPresetSpec {
            Kind = ListeningPresetKind.Custom, Id = "custom", Name = "Custom",
            Description = "User-tuned spatial DSP, EQ, and neural pipeline settings.",
            HeadphoneVirtualizer = true, MatrixUpmixing = false, CavernizeUpmix = false, AtmosAwareUpmix = true,
            IsmRoomEnabled = false, IsmRoomPresetIndex = 0, LoudnessNormModeIndex = 0, Iso226Enabled = false,
            SpatialKaraokeEnabled = false, NeuralSpatialUpmixModeIndex = 0, TransauralXtcEnabled = false, FilterStudioEnabled = false
        }
    ];

    /// <summary>Curated workspace presets tailoring startup tab, Pro toggles, and sound diagnostics density.</summary>
    public static IReadOnlyList<WorkspacePresetSpec> WorkspacePresets { get; } = [
        new WorkspacePresetSpec {
            Kind = WorkspacePresetKind.Listener, Id = "listener", Name = "Listener",
            Description = "Clean everyday playback layout starting on Library/Player.",
            DefaultTab = 0, ProModeDefault = false, ShowSoundDiagnostics = false
        },
        new WorkspacePresetSpec {
            Kind = WorkspacePresetKind.Karaoke, Id = "karaoke", Name = "Karaoke Stage",
            Description = "Sing-along stage layout centered on Player, lyrics, and vocal controls.",
            DefaultTab = 0, ProModeDefault = false, ShowSoundDiagnostics = false
        },
        new WorkspacePresetSpec {
            Kind = WorkspacePresetKind.StudioPro, Id = "studio_pro", Name = "Studio Pro",
            Description = "Mastering engineer workspace starting in Studio with Pro mode and sound diagnostics enabled.",
            DefaultTab = 2, ProModeDefault = true, ShowSoundDiagnostics = true
        }
    ];

    /// <summary>Finds a curated <see cref="ListeningPresetSpec"/> by id or enum name (case-insensitive), or <c>null</c> if unknown.</summary>
    public static ListeningPresetSpec FindListeningPreset(string id) {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string needle = id.Trim();
        for (int i = 0; i < ListeningPresets.Count; i++) {
            ListeningPresetSpec spec = ListeningPresets[i];
            if (string.Equals(spec.Id, needle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec.Kind.ToString(), needle, StringComparison.OrdinalIgnoreCase))
                return spec;
        }
        return null;
    }

    /// <summary>Finds a curated <see cref="WorkspacePresetSpec"/> by id or enum name (case-insensitive), or <c>null</c> if unknown.</summary>
    public static WorkspacePresetSpec FindWorkspacePreset(string id) {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string needle = id.Trim();
        for (int i = 0; i < WorkspacePresets.Count; i++) {
            WorkspacePresetSpec spec = WorkspacePresets[i];
            if (string.Equals(spec.Id, needle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec.Kind.ToString(), needle, StringComparison.OrdinalIgnoreCase))
                return spec;
        }
        return null;
    }

    /// <summary>Returns true when <paramref name="key"/> matches any secret, credential, token, password, or private key pattern.</summary>
    public static bool IsBlockedSecretKey(string key) {
        if (string.IsNullOrWhiteSpace(key)) return true;
        string trimmed = key.Trim();
        for (int i = 0; i < SecretSubstrings.Length; i++) {
            if (trimmed.Contains(SecretSubstrings[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (SafeConfigKeys.Contains(trimmed)) return false;
        if (ExactSecretKeys.Contains(trimmed)) return true;
        return trimmed.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("key", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("_key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Serializes non-secret preferences into a portable v1 JSON snapshot, stripping all secret keys.</summary>
    public static string SerializePortableJson(Dictionary<string, object> rawPrefs) =>
        SerializePortableJson(rawPrefs, null, null);

    /// <summary>Serializes non-secret preferences into a portable v1 JSON snapshot with explicit preset headers.</summary>
    public static string SerializePortableJson(
        Dictionary<string, object> rawPrefs,
        string activeListeningPresetId,
        string activeWorkspacePresetId) {
        SortedDictionary<string, object> sanitized = new(StringComparer.OrdinalIgnoreCase);
        string resolvedListening = activeListeningPresetId ?? string.Empty;
        string resolvedWorkspace = activeWorkspacePresetId ?? string.Empty;
        int strippedCount = 0;

        if (rawPrefs != null) {
            foreach (KeyValuePair<string, object> kv in rawPrefs) {
                if (IsBlockedSecretKey(kv.Key)) {
                    strippedCount++;
                    continue;
                }
                object cleanVal = SanitizeExportValue(kv.Value);
                if (cleanVal == null) continue;
                string k = kv.Key.Trim();
                sanitized[k] = cleanVal;
                if (string.IsNullOrEmpty(resolvedListening) &&
                    string.Equals(k, "activeListeningPresetId", StringComparison.OrdinalIgnoreCase)) {
                    resolvedListening = cleanVal.ToString() ?? string.Empty;
                } else if (string.IsNullOrEmpty(resolvedWorkspace) &&
                           string.Equals(k, "activeWorkspacePresetId", StringComparison.OrdinalIgnoreCase)) {
                    resolvedWorkspace = cleanVal.ToString() ?? string.Empty;
                }
            }
        }

        var payload = new {
            SchemaVersion = PortableSettingsSnapshot.CurrentSchemaVersion,
            ExportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            AppName = "Cavernize",
            ActiveListeningPresetId = string.IsNullOrWhiteSpace(resolvedListening) ? "custom" : resolvedListening,
            ActiveWorkspacePresetId = string.IsNullOrWhiteSpace(resolvedWorkspace) ? "listener" : resolvedWorkspace,
            StrippedSecretCount = strippedCount,
            Preferences = sanitized
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Parses a portable JSON settings snapshot, enforcing <c>SchemaVersion == 1</c> and rejecting/stripping any secret keys.
    /// </summary>
    public static PortableSettingsSnapshot DeserializePortableJson(string json) {
        if (!TryDeserializePortableJson(json, out PortableSettingsSnapshot snapshot, out string error)) {
            throw new InvalidDataException(error);
        }
        return snapshot;
    }

    /// <summary>Safely attempts to parse a portable JSON snapshot while rejecting/stripping any secret keys.</summary>
    public static bool TryDeserializePortableJson(
        string json,
        out PortableSettingsSnapshot snapshot,
        out string error) {
        snapshot = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) {
            error = "Portable settings JSON is empty.";
            return false;
        }
        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                error = "Portable settings JSON root must be a JSON object.";
                return false;
            }

            int schemaVer = PortableSettingsSnapshot.CurrentSchemaVersion;
            if (TryGetPropertyIgnoreCase(root, "SchemaVersion", out JsonElement verEl) &&
                verEl.ValueKind == JsonValueKind.Number &&
                verEl.TryGetInt32(out int parsedVer)) {
                schemaVer = parsedVer;
            }
            if (schemaVer != PortableSettingsSnapshot.CurrentSchemaVersion) {
                error = $"Unsupported portable settings SchemaVersion={schemaVer}.";
                return false;
            }

            string appName = TryGetStringIgnoreCase(root, "AppName") ?? "Cavernize";
            if (!string.Equals(appName, "Cavernize", StringComparison.OrdinalIgnoreCase)) {
                error = $"Unexpected AppName '{appName}' in portable settings snapshot.";
                return false;
            }

            string exportedUtc = TryGetStringIgnoreCase(root, "ExportedUtc") ?? string.Empty;
            string listeningId = TryGetStringIgnoreCase(root, "ActiveListeningPresetId") ?? "custom";
            string workspaceId = TryGetStringIgnoreCase(root, "ActiveWorkspacePresetId") ?? "listener";
            Dictionary<string, object> prefs = new(StringComparer.OrdinalIgnoreCase);
            int stripped = 0;

            if (TryGetPropertyIgnoreCase(root, "Preferences", out JsonElement prefsEl) &&
                prefsEl.ValueKind == JsonValueKind.Object) {
                foreach (JsonProperty prop in prefsEl.EnumerateObject()) {
                    if (IsBlockedSecretKey(prop.Name)) {
                        stripped++;
                        continue;
                    }
                    object val = ConvertJsonValue(prop.Value);
                    if (val != null) {
                        prefs[prop.Name.Trim()] = val;
                    }
                }
            }

            snapshot = new PortableSettingsSnapshot {
                SchemaVersion = schemaVer,
                ExportedUtc = exportedUtc,
                AppName = "Cavernize",
                ActiveListeningPresetId = string.IsNullOrWhiteSpace(listeningId) ? "custom" : listeningId.Trim(),
                ActiveWorkspacePresetId = string.IsNullOrWhiteSpace(workspaceId) ? "listener" : workspaceId.Trim(),
                StrippedSecretCount = stripped,
                Preferences = prefs
            };
            return true;
        } catch (JsonException ex) {
            error = $"Invalid portable settings JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>Clamps a floating-point preference cleanly, guarding against NaN/Infinity.</summary>
    public static float ClampFloat(float value, float min, float max) =>
        ClampFloat(value, min, max, min);

    /// <summary>Clamps a floating-point preference cleanly, returning clamped <paramref name="fallback"/> when non-finite.</summary>
    public static float ClampFloat(float value, float min, float max, float fallback) {
        if (min > max) {
            (min, max) = (max, min);
        }
        return float.IsFinite(value) ? Math.Clamp(value, min, max) : Math.Clamp(fallback, min, max);
    }

    /// <summary>Clamps an integer preference cleanly within <c>[min, max]</c>.</summary>
    public static int ClampInt(int value, int min, int max) {
        if (min > max) {
            (min, max) = (max, min);
        }
        return Math.Clamp(value, min, max);
    }

    /// <summary>Clamps a 64-bit integer preference safely into a 32-bit range <c>[min, max]</c>.</summary>
    public static int ClampInt(long value, int min, int max) {
        if (min > max) {
            (min, max) = (max, min);
        }
        return (int)Math.Clamp(value, min, max);
    }

    static object SanitizeExportValue(object raw) {
        if (raw == null) {
            return null;
        }
        if (raw is JsonElement je) {
            return ConvertJsonValue(je);
        }
        if (raw is float f) {
            return float.IsFinite(f) ? f : null;
        }
        if (raw is double d) {
            return double.IsFinite(d) ? d : null;
        }
        if (raw is bool || raw is int || raw is long || raw is string) {
            return raw;
        }
        if (raw is IEnumerable<string> set) {
            List<string> items = [];
            foreach (string s in set) {
                if (!string.IsNullOrEmpty(s)) {
                    items.Add(s);
                }
            }
            return items;
        }
        return raw.ToString();
    }

    static object ConvertJsonValue(JsonElement el) => el.ValueKind switch {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : (el.TryGetDouble(out double d) && double.IsFinite(d) ? d : 0.0),
        JsonValueKind.Array => el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).ToList(),
        _ => null
    };

    static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement val) {
        if (obj.TryGetProperty(name, out val)) {
            return true;
        }
        foreach (JsonProperty p in obj.EnumerateObject()) {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) {
                val = p.Value;
                return true;
            }
        }
        val = default;
        return false;
    }

    static string TryGetStringIgnoreCase(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
