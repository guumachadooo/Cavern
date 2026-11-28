using System.Globalization;
using System.IO;
using System.Text;

using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.CavernSettings;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    const int ReqSafRemoteCacheTree = 1013;
    const int ReqImportPortableJson = 1014;

    TextView presetsModalCacheStatusTv;
    long liveEngineSyncSeq;

    /// <summary>Synchronizes persisted <see cref="AppSettingsStore"/> DSP/audio state directly into live engines.</summary>
    void SyncFullStoreToLiveEngines(bool includeBinauralAndAutoEq) {
        if (store == null) return;
        store.ApplyGlobals();
        if (playerEngine != null) {
            playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
            playerEngine.DistanceSimulation = store.DistanceSimulation;
            playerEngine.VolumeRolloff = (Rolloffs)Math.Clamp(store.VolumeRolloffIndex, 0, 3);
            playerEngine.LFEVolume = store.LfeVolume;
            playerEngine.EnvironmentType = (Environments)Math.Clamp(store.EnvironmentTypeIndex, 0, 3);
            playerEngine.EnvironmentSize = new System.Numerics.Vector3(store.RoomSizeX, store.RoomSizeY, store.RoomSizeZ);
            playerEngine.AudioQuality = (QualityModes)Math.Clamp(store.AudioQualityIndex, 0, 3);
            playerEngine.UsbDacNativeSampleRateEnabled = store.UsbDacNativeSampleRate;
            playerEngine.HeadTrackingPredictionMs = store.HeadTrackingPredictionMs;
            playerEngine.PropagationDelay6DoFEnabled = store.PropagationDelay6DoFEnabled;

            UpmixingSettings upmixToSync = null;
            if (upmixingSettings != null) {
                upmixingSettings.MatrixUpmixing = store.MatrixUpmixing;
                upmixingSettings.Cavernize = store.CavernizeUpmix;
                upmixingSettings.AtmosAware = store.AtmosAwareUpmix;
                upmixToSync = upmixingSettings;
            }

            if (includeBinauralAndAutoEq) {
                BinauralProfileService.ApplySavedProfile(this, store);
                AutoEqService.ApplySavedToEngine(this, store, playerEngine);
            }
            BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
            AutoEqService.SyncBinauralModeToEngine(store, playerEngine);

            playerEngine.ApplyPersonalizationConfig(store.BuildPersonalizationConfig());
            playerEngine.ApplyLoudnessConfig(store.BuildLoudnessConfig());
            playerEngine.ApplyTransauralXtcConfig(store.BuildTransauralXtcConfig());
            if (audioDeviceProfileManager?.CurrentDevice is AudioDeviceRouteInfo rt) {
                playerEngine.UpdateOutputRouteForTransaural(rt.IsHeadphoneRoute, rt.Transport);
            }
            playerEngine.ApplyLgsrConfig(store.BuildLgsrConfig());
            NeuralAudioConfig neuralToSync = store.BuildNeuralAudioConfig();

            float[] savedVoiceprint = store.GetMicVoiceprintBands();
            if (savedVoiceprint != null) {
                playerEngine.VocalMixMatchStage.ApplyMicVoiceprint(savedVoiceprint, store.BuildVocalMacroSettings());
            } else {
                playerEngine.VocalMixMatchStage.ApplyVocalProfile(
                    playerEngine.VocalMixMatchStage.ActiveDna,
                    store.BuildVocalMacroSettings());
            }
            _ = SyncUpmixAndNeuralSequentiallyAsync(
                playerEngine,
                upmixToSync,
                neuralToSync,
                Interlocked.Increment(ref liveEngineSyncSeq));
        }
        if (includeBinauralAndAutoEq) {
            audioOutput?.ApplyProAudioConfig(store.BuildProAudioConfig());
            headTrackingManager?.ApplySettings();
        }
        SyncFilterStudioAndToolsFromStore();
        ApplyArbitratedExternalizationAndBrirBypass();
    }

    async Task SyncUpmixAndNeuralSequentiallyAsync(
        RealtimePlayerEngine engine,
        UpmixingSettings upmix,
        NeuralAudioConfig neural,
        long seq) {
        try {
            if (upmix != null) {
                await engine.ApplyUpmixingSettingsAsync(upmix).ConfigureAwait(false);
            }
            if (Volatile.Read(ref liveEngineSyncSeq) != seq) {
                return;
            }
            await engine.ApplyNeuralConfigAsync(neural).ConfigureAwait(false);
            if (Volatile.Read(ref liveEngineSyncSeq) != seq) {
                return;
            }
            RunOnUiThread(() => {
                if (!uiDestroyed && !IsFinishing && !IsDestroyed && Volatile.Read(ref liveEngineSyncSeq) == seq) {
                    SyncNeuralRuntimeDiagnostics();
                }
            });
        } catch (Exception ex) {
            AppLogger.Warn($"SyncUpmixAndNeuralSequentiallyAsync: {ex.Message}");
        }
    }

    /// <summary>Applies a curated <see cref="ListeningPresetSpec"/> and refreshes live DSP engines &amp; UI.</summary>
    void ApplyListeningPreset(string presetId) {
        ListeningPresetSpec spec = ListeningPresetCatalog.FindListeningPreset(presetId);
        if (spec == null || store == null) return;
        store.ActiveListeningPresetId = spec.Id;
        store.HeadphoneVirtualizer = spec.HeadphoneVirtualizer;
        store.MatrixUpmixing = spec.MatrixUpmixing;
        store.CavernizeUpmix = spec.CavernizeUpmix;
        store.AtmosAwareUpmix = spec.AtmosAwareUpmix;
        store.IsmRoomEnabled = spec.IsmRoomEnabled;
        store.IsmRoomPresetIndex = spec.IsmRoomPresetIndex;
        store.LoudnessNormModeIndex = spec.LoudnessNormModeIndex;
        store.Iso226Enabled = spec.Iso226Enabled;
        store.SpatialKaraokeEnabled = spec.SpatialKaraokeEnabled;
        store.NeuralSpatialUpmixModeIndex = spec.NeuralSpatialUpmixModeIndex;
        store.TransauralXtcEnabled = spec.TransauralXtcEnabled;
        store.FilterStudioEnabled = spec.FilterStudioEnabled;

        SyncFullStoreToLiveEngines(includeBinauralAndAutoEq: false);
        NotifyUiDomainChanged(UiStateDomain.All, immediate: true);
        RefreshFilterStudioSummaryUi();
        RefreshSoundHubCardsUi();
        ShowToast(string.Format(S["PresetAppliedFmt"], spec.Name));
    }

    /// <summary>Applies a curated <see cref="WorkspacePresetSpec"/> (Pro Lab toggle &amp; default tab).</summary>
    void ApplyWorkspacePreset(string presetId) {
        WorkspacePresetSpec spec = ListeningPresetCatalog.FindWorkspacePreset(presetId);
        if (spec == null || store == null) return;
        store.ActiveWorkspacePresetId = spec.Id;
        SetSettingsProMode(spec.ProModeDefault);
        SwitchTab(spec.DefaultTab);
        ShowToast(string.Format(S["PresetAppliedFmt"], spec.Name));
    }

    /// <summary>Opens the One UI 9 SecQSDetail modal for Presets, JSON Export/Import, SAF Cache &amp; Symbiosis.</summary>
    void ShowPresetsAndPortabilityModal() {
        if (uiDestroyed || store == null) return;
        LinearLayout root = new(this) { Orientation = Orientation.Vertical };
        void AddHeader(string text, int topDp) {
            TextView tv = Label(text, 12.8f, OneUiTheme.TextMuted);
            tv.Typeface = OneUiTheme.GetAppleFontBold(this);
            tv.SetPadding(0, Dp(topDp), 0, Dp(5));
            root.AddView(tv);
        }
        static Button MakeWrapBtn(Button b) { b.SetSingleLine(false); b.SetMaxLines(2); b.Ellipsize = null; return b; }
        void StylePill(Button b, bool active) {
            MakeWrapBtn(b);
            b.Background = active
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTextColor(active ? OneUiTheme.OnPrimary : ColText);
            b.Typeface = active ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
        }

        // 1. Listening Presets
        AddHeader(S["ListeningPresetsLabel"], 0);
        List<(string id, Button btn)> listenBtns = [];
        void RefreshListen() {
            string cur = store.ActiveListeningPresetId ?? "custom";
            for (int i = 0; i < listenBtns.Count; i++) StylePill(listenBtns[i].btn, string.Equals(listenBtns[i].id, cur, StringComparison.OrdinalIgnoreCase));
        }
        IReadOnlyList<ListeningPresetSpec> lSpecs = ListeningPresetCatalog.ListeningPresets;
        for (int s = 0; s < lSpecs.Count; s += 3) {
            LinearLayout row = HRow();
            row.SetPadding(0, 0, 0, Dp(6));
            int n = Math.Min(3, lSpecs.Count - s);
            for (int j = 0; j < n; j++) {
                ListeningPresetSpec spec = lSpecs[s + j];
                string id = spec.Id;
                Button b = PillButton(spec.Name, () => { ApplyListeningPreset(id); RefreshListen(); });
                StylePill(b, string.Equals(id, store.ActiveListeningPresetId, StringComparison.OrdinalIgnoreCase));
                listenBtns.Add((id, b));
                row.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = j < n - 1 ? Dp(6) : 0 });
            }
            root.AddView(row);
        }

        // 2. Workspace Presets
        AddHeader(S["WorkspacePresetsLabel"], 6);
        List<(string id, Button btn)> wsBtns = [];
        void RefreshWs() {
            string cur = store.ActiveWorkspacePresetId ?? "listener";
            for (int i = 0; i < wsBtns.Count; i++) StylePill(wsBtns[i].btn, string.Equals(wsBtns[i].id, cur, StringComparison.OrdinalIgnoreCase));
        }
        LinearLayout wsRow = HRow();
        wsRow.SetPadding(0, 0, 0, Dp(8));
        IReadOnlyList<WorkspacePresetSpec> wSpecs = ListeningPresetCatalog.WorkspacePresets;
        for (int i = 0; i < wSpecs.Count; i++) {
            WorkspacePresetSpec spec = wSpecs[i];
            string id = spec.Id;
            Button b = PillButton(spec.Name, () => { ApplyWorkspacePreset(id); RefreshWs(); });
            StylePill(b, string.Equals(id, store.ActiveWorkspacePresetId, StringComparison.OrdinalIgnoreCase));
            wsBtns.Add((id, b));
            wsRow.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = i < wSpecs.Count - 1 ? Dp(6) : 0 });
        }
        root.AddView(wsRow);
        AddDivider(root);

        // 3. Portable JSON Backup
        AddHeader(S["PortableSettingsTitle"], 8);
        LinearLayout portRow = HRow();
        portRow.SetPadding(0, 0, 0, Dp(8));
        portRow.AddView(MakeWrapBtn(PrimaryButton(S["PortableExportBtn"], ExportPortableSettingsJson)), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        portRow.AddView(MakeWrapBtn(PillButton(S["PortableImportBtn"], StartPortableSettingsImportPicker)), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(portRow);
        AddDivider(root);

        // 4. SAF / Drive Remote Neural Cache
        AddHeader(S["RemoteCacheTitle"], 8);
        presetsModalCacheStatusTv = Label(FormatRemoteCacheUriDisplay(store.RemoteNeuralCacheTreeUri), 11.5f, OneUiTheme.TextSub);
        presetsModalCacheStatusTv.SetPadding(0, 0, 0, Dp(6));
        root.AddView(presetsModalCacheStatusTv);
        AddSwitchRow(root, S["RemoteCacheAutoSwitch"], store.RemoteNeuralCacheAutoSync, v => store.RemoteNeuralCacheAutoSync = v);
        LinearLayout cacheRow = HRow();
        cacheRow.SetPadding(0, Dp(6), 0, Dp(8));
        cacheRow.AddView(MakeWrapBtn(PillButton(S["RemoteCachePickBtn"], StartRemoteCacheFolderPicker)), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        cacheRow.AddView(MakeWrapBtn(PillButton(S["RemoteCacheSyncBtn"], () => TriggerRemoteCacheSync(presetsModalCacheStatusTv))), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(cacheRow);
        AddDivider(root);

        // 5. Accelerator Symbiosis
        AddHeader(S["SymbiosisCapsLabel"], 8);
        string activeProvider = string.IsNullOrWhiteSpace(neuralRuntime?.LastActiveProviderName) ? "CPU" : neuralRuntime.LastActiveProviderName;
        QnnHardwareCapabilitySnapshot qnnCaps = OnnxQnnContextCacheManager.ProbeDeviceQnnCapabilities(this);
        root.AddView(Label($"{activeProvider} · {qnnCaps.Summary}", 11.6f, OneUiTheme.TextSub));

        OneUiModalDialog.Show(this, S["PresetsSheetTitle"], customContent: root,
            icon: OneUiIconType.Sliders, iconBadgeColor: OneUiTheme.IconSpatial, positiveText: S["CloseBtn"]);
    }

    Dictionary<string, object> CollectPortablePrefsSnapshot() {
        Dictionary<string, object> result = new(StringComparer.OrdinalIgnoreCase);
        IDictionary<string, object> raw = GetSharedPreferences("cavernize_prefs", FileCreationMode.Private)?.All;
        if (raw == null) return result;
        foreach (KeyValuePair<string, object> kv in raw) {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
            object clean = UnboxPrefValue(kv.Value);
            if (clean != null) result[kv.Key] = clean;
        }
        return result;
    }

    static object UnboxPrefValue(object val) => val switch {
        bool or int or long or float or double or string => val,
        Java.Lang.Boolean jb => jb.BooleanValue(),
        Java.Lang.Integer ji => ji.IntValue(),
        Java.Lang.Long jl => jl.LongValue(),
        Java.Lang.Float jf => jf.FloatValue(),
        Java.Lang.Double jd => jd.DoubleValue(),
        Java.Lang.String js => js.ToString(),
        IEnumerable<string> strSet => strSet.ToList(),
        _ => val.ToString()
    };

    void ExportPortableSettingsJson() {
        if (store == null) return;
        try {
            Dictionary<string, object> snapshot = CollectPortablePrefsSnapshot();
            int strippedCount = snapshot.Keys.Count(ListeningPresetCatalog.IsBlockedSecretKey);
            string json = ListeningPresetCatalog.SerializePortableJson(snapshot, store.ActiveListeningPresetId, store.ActiveWorkspacePresetId);
            string baseDir = AndroidFileHelper.GetExportWorkingDir(this, ExportStorageDestination.DownloadsCavernize, ".json", out _, out _);
            string outDir = Path.Combine(baseDir, "Presets");
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, $"Cavernize_Settings_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.json");
            File.WriteAllText(outPath, json, Encoding.UTF8);
            AndroidFileHelper.PublishToDownloads(this, outPath, "Presets");
            ShowToast(string.Format(S["PortableExportDoneFmt"], Path.GetFileName(outPath), strippedCount));
            ShareLocalFile(outPath);
        } catch (Exception ex) {
            AppLogger.Warn($"ExportPortableSettingsJson failed: {ex.Message}");
            ShowToast(ex.Message);
        }
    }

    void StartPortableSettingsImportPicker() {
        try {
            Intent intent = new(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/json", "text/plain" });
            StartActivityForResult(intent, ReqImportPortableJson);
        } catch (Exception ex) {
            ShowToast(string.Format(S["PortableImportFailToast"], ex.Message));
        }
    }

    void ApplyImportedPortableSnapshot(PortableSettingsSnapshot snap) {
        if (snap == null || store == null) return;
        ISharedPreferencesEditor editor = GetSharedPreferences("cavernize_prefs", FileCreationMode.Private)?.Edit();
        if (editor == null) return;

        Dictionary<string, SettingDescriptor> safeByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (SettingDescriptor d in AppSettingsStore.SchemaDescriptors) {
            if (!d.IsSecret && d.IsWritable && !string.IsNullOrEmpty(d.PrefKey)) safeByKey[d.PrefKey] = d;
        }
        int appliedCount = 0;
        foreach (KeyValuePair<string, object> kv in snap.Preferences) {
            if (!ListeningPresetCatalog.IsBlockedSecretKey(kv.Key) &&
                safeByKey.TryGetValue(kv.Key, out SettingDescriptor desc) &&
                WriteSafePreference(editor, desc, kv.Value)) {
                appliedCount++;
            }
        }
        if (ListeningPresetCatalog.FindListeningPreset(snap.ActiveListeningPresetId) != null)
            editor.PutString("activeListeningPresetId", snap.ActiveListeningPresetId);
        if (ListeningPresetCatalog.FindWorkspacePreset(snap.ActiveWorkspacePresetId) != null)
            editor.PutString("activeWorkspacePresetId", snap.ActiveWorkspacePresetId);
        editor.Apply();

        SyncFullStoreToLiveEngines(includeBinauralAndAutoEq: true);
        RefreshAllUiState();
        RefreshSoundHubCardsUi();
        ShowToast(string.Format(S["PortableImportDoneFmt"], appliedCount, store.ActiveListeningPresetId));
    }

    static bool WriteSafePreference(ISharedPreferencesEditor editor, SettingDescriptor desc, object rawVal) {
        if (rawVal == null) return false;
        try {
            switch (desc.Kind) {
                case SettingValueKind.Bool:
                    editor.PutBoolean(desc.PrefKey, rawVal is bool bv ? bv : Convert.ToBoolean(rawVal, CultureInfo.InvariantCulture));
                    return true;
                case SettingValueKind.Int:
                    long iv = rawVal is long l1 ? l1 : Convert.ToInt64(rawVal, CultureInfo.InvariantCulture);
                    editor.PutInt(desc.PrefKey, ListeningPresetCatalog.ClampInt(iv, int.MinValue, int.MaxValue));
                    return true;
                case SettingValueKind.Long:
                    editor.PutLong(desc.PrefKey, rawVal is long l2 ? l2 : Convert.ToInt64(rawVal, CultureInfo.InvariantCulture));
                    return true;
                case SettingValueKind.Float:
                case SettingValueKind.Double:
                    float fv = rawVal is float f1 ? f1 : Convert.ToSingle(rawVal, CultureInfo.InvariantCulture);
                    editor.PutFloat(desc.PrefKey, ListeningPresetCatalog.ClampFloat(fv, -1e6f, 1e6f, 0f));
                    return true;
                case SettingValueKind.String:
                    editor.PutString(desc.PrefKey, rawVal.ToString() ?? "");
                    return true;
                case SettingValueKind.StringSet:
                    if (rawVal is IEnumerable<string> seq) { editor.PutStringSet(desc.PrefKey, seq.ToHashSet(StringComparer.OrdinalIgnoreCase)); return true; }
                    return false;
                default: return false;
            }
        } catch { return false; }
    }

    void StartRemoteCacheFolderPicker() {
        try {
            Intent intent = new(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
            StartActivityForResult(intent, ReqSafRemoteCacheTree);
        } catch (Exception ex) { ShowToast(ex.Message); }
    }

    void TriggerRemoteCacheSync(TextView statusTv) {
        if (store == null || neuralTrackCache == null) return;
        if (string.IsNullOrWhiteSpace(store.RemoteNeuralCacheTreeUri)) { ShowToast(S["RemoteCacheNone"]); return; }
        Android.Net.Uri treeUri = Android.Net.Uri.Parse(store.RemoteNeuralCacheTreeUri);
        if (treeUri == null) { ShowToast(S["RemoteCacheNone"]); return; }
        _ = Task.Run(async () => {
            try {
                RemoteSafCacheSyncResult res = await neuralTrackCache.SyncRemoteCacheAsync(this, treeUri).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    if (statusTv != null) statusTv.Text = res.Summary;
                    ShowToast(string.Format(S["RemoteCacheSyncedFmt"], res.ImportedCount, res.ExportedCount));
                });
            } catch (Exception ex) { RunOnUiThread(() => ShowToast(ex.Message)); }
        });
    }

    string FormatRemoteCacheUriDisplay(string uriStr) {
        if (string.IsNullOrWhiteSpace(uriStr)) return S["RemoteCacheNone"];
        try { return Android.Net.Uri.Parse(uriStr)?.LastPathSegment ?? uriStr; }
        catch { return uriStr; }
    }

    /// <summary>Handles Epoch 5 SAF directory tree selection and portable JSON import results.</summary>
    bool TryHandleEpoch5ActivityResult(int requestCode, Result resultCode, Intent data) {
        if (resultCode != Result.Ok || data == null) return false;
        if (requestCode == ReqSafRemoteCacheTree) {
            if (data.Data != null) {
                ActivityFlags takeFlags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                if (takeFlags == 0) takeFlags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
                try { ContentResolver?.TakePersistableUriPermission(data.Data, takeFlags); }
                catch (Exception ex) { AppLogger.Warn($"TakePersistableUriPermission: {ex.Message}"); }
                if (store != null) store.RemoteNeuralCacheTreeUri = data.Data.ToString() ?? "";
                if (presetsModalCacheStatusTv != null) presetsModalCacheStatusTv.Text = FormatRemoteCacheUriDisplay(store?.RemoteNeuralCacheTreeUri);
                if (store?.RemoteNeuralCacheAutoSync == true) TriggerRemoteCacheSync(presetsModalCacheStatusTv);
            }
            return true;
        }
        if (requestCode == ReqImportPortableJson) {
            if (data.Data != null) {
                Android.Net.Uri uri = data.Data;
                _ = Task.Run(() => {
                    try {
                        using Stream stream = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("Empty URI stream.");
                        using StreamReader reader = new(stream, Encoding.UTF8);
                        if (!ListeningPresetCatalog.TryDeserializePortableJson(reader.ReadToEnd(), out PortableSettingsSnapshot snap, out string err)) {
                            RunOnUiThread(() => ShowToast(string.Format(S["PortableImportFailToast"], err)));
                            return;
                        }
                        RunOnUiThread(() => { if (!uiDestroyed) ApplyImportedPortableSnapshot(snap); });
                    } catch (Exception ex) {
                        RunOnUiThread(() => ShowToast(string.Format(S["PortableImportFailToast"], ex.Message)));
                    }
                });
            }
            return true;
        }
        return false;
    }
}
