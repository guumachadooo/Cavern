using System.Globalization;

using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.Virtualizer;

using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

public partial class MainActivity {
    readonly UiStateCoordinator uiCoordinator = new();
    bool uiCoordinatorInitialized;
    bool runtimeBrirBypassedForSpeaker;
    int speakerBrirBypassGen;
    LinearLayout roomProfRowRef, roomSpeakerXtcRow;
    TextView roomSpeakerXtcPresetText;
    bool? lastSpeakerRouteBento;

    bool IsCoordinatorUpdating => uiCoordinator.IsUpdatingFromCoordinator;

    void InitializeUiCoordinator() {
        uiCoordinator.SetDispatcher(action => {
            if (!uiDestroyed && uiHandler != null) {
                uiHandler.Post(action);
            }
        });
        uiCoordinator.ClearSubscriptions();

        uiCoordinator.Subscribe(UiStateDomain.Volume, () => SyncUiFromSystemMediaVolume(updateEngine: false), order: 10);
        uiCoordinator.Subscribe(UiStateDomain.SpatialOutput, () => {
            RefreshBinauralProfileUi();
            RefreshAutoEqUi();
            RefreshHeadTrackingUi();
            RefreshExternalizationUi();
            RefreshTransauralXtcUi();
            RefreshPersonalizationUi();
            RefreshProAudioUi();
            refreshBentoTilesUi?.Invoke();
        }, order: 20);
        uiCoordinator.Subscribe(UiStateDomain.Upmix, () => {
            SyncUpmixModeUi();
            RefreshSpatialChoreoClarifierUi();
        }, order: 30);
        uiCoordinator.Subscribe(UiStateDomain.Neural, () => {
            RefreshNeuralAudioUi();
            RefreshNeuralPrepUi();
            RefreshLgsrUi();
            RefreshExtractAiChainPreviewUi();
        }, order: 40);
        uiCoordinator.Subscribe(UiStateDomain.Room3D, () => {
            RefreshRoomTransportButtons();
            RefreshSignalChainBarUi();
            RefreshFilterButtonsUi();
            SyncRoomInvertOrbitUi();
            RefreshPlayerSourcesList();
            UpdateRoomCanvas();
        }, order: 50);
        uiCoordinator.Subscribe(UiStateDomain.Playback, () => {
            RefreshMusicPlayerUi();
            RefreshMusicPlayerControlsUi();
            RefreshLyricsSingDockUi();
            RefreshBottomActionBarVisuals();
        }, order: 60);
        uiCoordinator.Subscribe(UiStateDomain.StudioExport, () => {
            RefreshConverterTabState();
            RefreshExtractorChecklist();
        }, order: 70);
        uiCoordinator.Subscribe(UiStateDomain.Settings, () => {
            RefreshLoudnessUi();
            RefreshSettingsTabButtons();
            RefreshSettingsCategorySubtitles();
            RefreshSettingsHubVisibility();
            RefreshSettingsDot();
            RefreshFFmpegCardUi();
            RefreshTruehddCardUi();
            RefreshSettingsUpdateUi();
            RefreshLogSummaryLabel();
        }, order: 80);

        uiCoordinatorInitialized = true;
    }

    void NotifyUiDomainChanged(UiStateDomain domains, bool immediate = false) {
        if (uiDestroyed) {
            return;
        }
        if (!uiCoordinatorInitialized) {
            InitializeUiCoordinator();
        }
        uiCoordinator.NotifyChanged(domains, immediate);
    }

    bool IsActiveOutputSpeakerRoute() {
        AudioDeviceRouteInfo route = audioDeviceProfileManager?.CurrentDevice;
        return route != null && !route.IsHeadphoneRoute;
    }

    bool IsSpeakerXtcOutputActive() =>
        IsActiveOutputSpeakerRoute() &&
        ((store?.TransauralXtcEnabled == true) || (playerEngine?.TransauralXtcDiagnostics.IsActive == true));

    bool ShouldAutoBypassRoomAmbienceForSpeakers() =>
        IsActiveOutputSpeakerRoute() || (playerEngine?.TransauralXtcDiagnostics.IsActive == true);

    PhysicalExternalizationConfig BuildArbitratedExternalizationConfig() {
        PhysicalExternalizationConfig cfg = store != null
            ? store.BuildExternalizationConfig()
            : PhysicalExternalizationConfig.Default;
        if (ShouldAutoBypassRoomAmbienceForSpeakers() && cfg.IsmRoomEnabled) {
            return new PhysicalExternalizationConfig {
                IsmRoomEnabled = false,
                RoomPreset = cfg.RoomPreset,
                WallMaterial = cfg.WallMaterial,
                ReflectionOrder = cfg.ReflectionOrder,
                ReflectionLevel = cfg.ReflectionLevel,
                NearFieldEnabled = false,
                AirAbsorptionEnabled = cfg.AirAbsorptionEnabled,
                AirTemperatureCelsius = cfg.AirTemperatureCelsius,
                AirRelativeHumidityPct = cfg.AirRelativeHumidityPct
            };
        }
        return cfg;
    }

    void ApplyArbitratedExternalizationConfigOnly() {
        if (store == null || playerEngine == null) {
            return;
        }
        playerEngine.ApplyExternalizationConfig(BuildArbitratedExternalizationConfig());
    }

    void ApplyArbitratedExternalizationAndBrirBypass() {
        if (store == null || playerEngine == null) {
            return;
        }
        bool bypassForSpeakers = ShouldAutoBypassRoomAmbienceForSpeakers();
        ApplyArbitratedExternalizationConfigOnly();

        bool brirSelected = BinauralProfileService.IsActiveProfileBrir(store);
        if (bypassForSpeakers && brirSelected && !runtimeBrirBypassedForSpeaker) {
            runtimeBrirBypassedForSpeaker = true;
            playerEngine.SceneHub.IsMeasuredBrirActive = false;
            int myGen = Interlocked.Increment(ref speakerBrirBypassGen);
            _ = Task.Run(() => {
                try {
                    (VirtualChannel[] ch, int sr, float refDist, _, _, _) =
                        BinauralProfileService.LoadBundledProfileForProbe(
                            this,
                            BinauralProfileService.DefaultProfileId,
                            store.MinimumPhaseHrir,
                            store.BuildPersonalizationConfig());
                    if (ch != null && myGen == Volatile.Read(ref speakerBrirBypassGen) && !uiDestroyed) {
                        VirtualizerFilter.Override(ch, sr, refDist);
                        RunOnUiThread(() => {
                            if (uiDestroyed || !runtimeBrirBypassedForSpeaker || playerEngine == null) {
                                return;
                            }
                            playerEngine.SceneHub.IsMeasuredBrirActive = false;
                            ApplyArbitratedExternalizationConfigOnly();
                            NotifyUiDomainChanged(UiStateDomain.SpatialOutput | UiStateDomain.Room3D);
                        });
                    }
                } catch (Exception ex) {
                    AppLogger.Warn($"Speaker BRIR bypass fallback: {ex.Message}");
                }
            });
        } else if (!bypassForSpeakers && runtimeBrirBypassedForSpeaker) {
            runtimeBrirBypassedForSpeaker = false;
            int myGen = Interlocked.Increment(ref speakerBrirBypassGen);
            _ = Task.Run(() => {
                try {
                    BinauralProfileService.ApplySavedProfile(this, store);
                    if (myGen == Volatile.Read(ref speakerBrirBypassGen) && !uiDestroyed) {
                        RunOnUiThread(() => {
                            if (uiDestroyed || runtimeBrirBypassedForSpeaker || playerEngine == null) {
                                return;
                            }
                            BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                            ApplyArbitratedExternalizationConfigOnly();
                            NotifyUiDomainChanged(UiStateDomain.SpatialOutput | UiStateDomain.Room3D | UiStateDomain.Playback | UiStateDomain.Settings);
                        });
                    }
                } catch (Exception ex) {
                    AppLogger.Warn($"Headphone BRIR restore: {ex.Message}");
                }
            });
        }
    }

    void SetHeadphoneVirtualizerState(bool enabled) {
        if (store == null || IsCoordinatorUpdating) {
            return;
        }
        store.HeadphoneVirtualizer = enabled;
        if (playerEngine != null) {
            playerEngine.HeadphoneVirtualizer = enabled;
            BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
        }
        AutoEqService.SyncBinauralModeToEngine(store, playerEngine);
        ApplyArbitratedExternalizationAndBrirBypass();
        NotifyUiDomainChanged(
            UiStateDomain.SpatialOutput | UiStateDomain.Upmix | UiStateDomain.Room3D | UiStateDomain.Playback | UiStateDomain.Settings,
            immediate: true);
    }

    void ToggleActiveOutputSpatialization() {
        if (store == null || IsCoordinatorUpdating) {
            return;
        }
        if (IsActiveOutputSpeakerRoute()) {
            store.TransauralXtcEnabled = !store.TransauralXtcEnabled;
            ApplyAndRefreshTransauralXtc();
            return;
        }
        SetHeadphoneVirtualizerState(!store.HeadphoneVirtualizer);
    }

    void OpenActiveOutputSpatializationSheet() {
        if (IsActiveOutputSpeakerRoute()) {
            ShowSpeakerXtcQuickDialog();
        } else {
            ShowBinauralProfileSelectorDialog();
        }
    }

    bool ShouldHideChannelUpmixSelectors() {
        if (IsActiveTrackMultichannelOrAtmos()) {
            return true;
        }
        return (store != null && store.NeuralSpatialUpmixModeIndex != (int)NeuralSpatialUpmixMode.Off) ||
               (playerEngine?.IsLiveNeuralStemPlaybackActive == true);
    }

    void SetClassicUpmixMode(int modeIdx) {
        if (store == null || IsCoordinatorUpdating) {
            return;
        }
        int clamped = Math.Clamp(modeIdx, 0, 2);
        store.MatrixUpmixing = clamped >= 1;
        store.CavernizeUpmix = clamped >= 2;
        if (upmixingSettings != null) {
            upmixingSettings.MatrixUpmixing = store.MatrixUpmixing;
            upmixingSettings.Cavernize = store.CavernizeUpmix;
        }
        _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
        NotifyUiDomainChanged(
            UiStateDomain.Upmix | UiStateDomain.Room3D | UiStateDomain.Playback | UiStateDomain.Settings,
            immediate: true);
    }

    void SetAtmosAwareUpmix(bool enabled) {
        if (store == null || IsCoordinatorUpdating) {
            return;
        }
        store.AtmosAwareUpmix = enabled;
        if (upmixingSettings != null) {
            upmixingSettings.AtmosAware = enabled;
        }
        _ = playerEngine?.ApplyUpmixingSettingsAsync(upmixingSettings);
        NotifyUiDomainChanged(UiStateDomain.Upmix | UiStateDomain.Playback | UiStateDomain.Settings, immediate: true);
    }

    void SetNeuralSpatialUpmixMode(int modeIdx, bool autoPrepareIfMissing = true) {
        if (store == null || IsCoordinatorUpdating) {
            return;
        }
        int clamped = Math.Clamp(modeIdx, 0, 2);
        store.NeuralSpatialUpmixModeIndex = clamped;
        _ = playerEngine?.ApplyNeuralConfigAsync(store.BuildNeuralAudioConfig());
        SyncNeuralRuntimeDiagnostics();
        NotifyUiDomainChanged(
            UiStateDomain.Upmix | UiStateDomain.Neural | UiStateDomain.Room3D | UiStateDomain.Playback | UiStateDomain.Settings,
            immediate: true);
        if (autoPrepareIfMissing &&
            clamped != 0 &&
            !(playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems ?? false) &&
            GetActiveTrack() != null) {
            PrepareActiveTrackNeuralStemsAsync(NeuralPipelineGoal.StemSeparation, silentIfNotReady: false);
        }
    }

    string GetTransauralPresetDisplayName(int presetIdx) => Math.Clamp(presetIdx, 0, 3) switch {
        1 => S["TransauralPresetPhonePort"],
        2 => S["TransauralPresetDesktop"],
        3 => S["TransauralPresetHiFi"],
        _ => S["TransauralPresetPhoneLand"]
    };

    void ShowSpeakerXtcQuickDialog() {
        if (uiDestroyed || store == null) {
            return;
        }
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        Dialog activeDlg = null;

        TextView statusTv = Label(S["ExtStatusSpeakerBypass"], 11.4f, OneUiTheme.PeachText);
        statusTv.SetPadding(0, 0, 0, Dp(8));
        body.AddView(statusTv);

        AddSwitchRow(body, S["TransauralEnableTitle"], store.TransauralXtcEnabled, v => {
            if (IsCoordinatorUpdating) return;
            store.TransauralXtcEnabled = v;
            ApplyAndRefreshTransauralXtc();
        }, helpBody: S["TipTransauralXtc"]);

        OneUiInlineSelector presetSel = new(this, S["TransauralPresetTitle"]);
        presetSel.SetItems(
            [
                S["TransauralPresetPhoneLand"],
                S["TransauralPresetPhonePort"],
                S["TransauralPresetDesktop"],
                S["TransauralPresetHiFi"]
            ],
            Math.Clamp(store.TransauralXtcPresetIndex, 0, 3));
        presetSel.SelectionChanged += pos => {
            if (IsCoordinatorUpdating) return;
            store.TransauralXtcPresetIndex = pos;
            ApplyAndRefreshTransauralXtc();
        };
        body.AddView(WrapControlWithHelp(presetSel, S["TransauralPresetTitle"].TrimEnd(':'), S["TipTransauralPreset"], bottomMarginDp: 6));

        AddSliderRow(
            body,
            S["TransauralStrengthTitle"],
            20,
            100,
            Math.Clamp((int)Math.Round(store.TransauralXtcStrength * 100f), 20, 100),
            v => {
                if (IsCoordinatorUpdating) return;
                store.TransauralXtcStrength = v * 0.01f;
                ApplyAndRefreshTransauralXtc();
            },
            "%",
            defaultValue: 85);

        AddSwitchRow(body, S["TransauralAutoBypassTitle"], store.TransauralXtcAutoBypassHeadphones, v => {
            if (IsCoordinatorUpdating) return;
            store.TransauralXtcAutoBypassHeadphones = v;
            ApplyAndRefreshTransauralXtc();
        }, helpBody: S["TipTransauralAutoBypass"]);

        activeDlg = OneUiModalDialog.Show(
            this,
            S["TransauralCardTitle"],
            customContent: body,
            icon: OneUiIconType.Speaker,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["CloseBtn"],
            negativeText: S["SettingsCatHeadphonesTitle"],
            onNegative: () => {
                activeDlg?.Dismiss();
                JumpToSettingsCategory(1);
            });
    }

    static string[] GetMvsepPresetStemMap(MvsepAlgorithmPreset preset) {
        if (preset == null) {
            return ["vocals", "instrumental"];
        }
        return preset.Id switch {
            "mvsep_cascade_6stem_karaoke" or "mvsep_ensemble_all_in" =>
                ["lead_vocal", "backing_vocals", "drums", "bass", "guitar", "piano", "other"],
            "mvsep_cascade_roformer124_karaoke" =>
                ["lead_vocal", "backing_vocals", "instrumental"],
            "mvsep_karaoke_lead_back" or "mvsep_karaoke_vocals_only_stage2" =>
                ["lead_vocal", "backing_vocals"],
            "mvsep_bs_roformer_6stem" or "mvsep_demucs4_6stem" =>
                ["drums", "bass", "guitar", "piano", "other", "vocals"],
            "mvsep_ensemble_5stem" =>
                ["drums", "bass", "piano", "other", "vocals"],
            _ => ["vocals", "instrumental"]
        };
    }

    static string TruncateAiPickerLabel(string text) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }
        return text.Length <= 28 ? text : text[..27] + "…";
    }

    string BuildActiveAiPickerSummaryText() {
        if (store == null) {
            return string.Empty;
        }
        NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        if (cfg.ExecutionBackend == NeuralExecutionBackend.MvsepCloud) {
            MvsepAlgorithmPreset preset = MvsepAlgorithmPreset.FindOrDefault(store.MvsepPresetId);
            return TruncateAiPickerLabel($"{S["AiPickerCloudIsolatedTag"]}: {preset.DisplayName}");
        }
        if (cfg.OrchestrationMode == NeuralOrchestrationMode.Manual) {
            NeuralModelCatalogEntry entry =
                OnnxRuntimeInstallerService.GetAllRegisteredModels(this)
                    .FirstOrDefault(m => string.Equals(m.Id, cfg.ActiveStemModelId, StringComparison.OrdinalIgnoreCase)) ??
                NeuralModelCatalog.FindById(cfg.ActiveStemModelId);
            if (entry != null) {
                return TruncateAiPickerLabel($"{S["AiPickerManualIsolatedTag"]}: {entry.DisplayName}");
            }
        }
        string bundleTag = Math.Clamp(store.NeuralStudioBundleIndex, 0, 4) switch {
            1 => S["StudioBundleVocalEnsemble"],
            2 => S["StudioBundlePureInstrumental"],
            3 => S["StudioBundleFullBand6Stem"],
            4 => S["StudioBundleKaraoke2Stage"],
            _ => S["StudioBundleFastSingle"]
        };
        return TruncateAiPickerLabel($"{S["AiPickerAutoBundleTag"]}: {bundleTag}");
    }

    void ShowUnifiedAiModelPickerDialog(Action onApplied = null) {
        if (uiDestroyed || store == null) {
            return;
        }
        Dialog dialog = null;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView subTv = Label(S["AiPickerSubtitle"], 11.8f, OneUiTheme.TextSecondary);
        subTv.SetPadding(0, 0, 0, Dp(8));
        content.AddView(subTv);

        NeuralAudioConfig curCfg = store.BuildNeuralAudioConfig();
        bool isCloudMode = curCfg.ExecutionBackend == NeuralExecutionBackend.MvsepCloud;
        bool isManualMode = curCfg.OrchestrationMode == NeuralOrchestrationMode.Manual;

        TextView modeBadgeTv = Label(BuildActiveAiPickerSummaryText(), 11.4f, OneUiTheme.PrimaryHigh);
        modeBadgeTv.SetTypeface(null, TypefaceStyle.Bold);
        modeBadgeTv.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        modeBadgeTv.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 36), 12f, OneUiTheme.WithAlpha(OneUiTheme.Primary, 120), 1f);
        content.AddView(modeBadgeTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        if (isCloudMode || isManualMode) {
            Button autoBtn = PillButton(S["AiPickerReturnAutoBtn"], () => {
                store.NeuralOrchestrationModeIndex = (int)NeuralOrchestrationMode.Auto;
                store.NeuralExecutionBackendIndex = (int)NeuralExecutionBackend.OnDevice;
                ApplyAndRefreshNeuralAudio();
                NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.StudioExport | UiStateDomain.Playback | UiStateDomain.Room3D, immediate: true);
                ShowToast(S["AiPickerReturnAutoToast"]);
                dialog?.Dismiss();
                onApplied?.Invoke();
            });
            content.AddView(autoBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });
        }

        // Section 1: On-Device ONNX Models (1-tap selects Manual isolation so StudioBundlePreset never overrides)
        TextView localHdr = Label(S["AiPickerSectionLocal"], 12.2f, OneUiTheme.PeachText);
        localHdr.SetTypeface(null, TypefaceStyle.Bold);
        localHdr.SetPadding(Dp(2), Dp(4), Dp(2), Dp(6));
        content.AddView(localHdr);

        List<NeuralModelCatalogEntry> localModels = OnnxRuntimeInstallerService.GetAllRegisteredModels(this);
        for (int i = 0; i < localModels.Count; i++) {
            NeuralModelCatalogEntry entry = localModels[i];
            if (entry.Task is not (NeuralModelTask.StemSeparation4 or NeuralModelTask.StemSeparation6
                or NeuralModelTask.VocalSeparation or NeuralModelTask.KaraokeLeadBacking)) {
                continue;
            }
            bool installed = OnnxRuntimeInstallerService.IsModelInstalled(this, entry.Id);
            bool isSelected = !isCloudMode && isManualMode &&
                              curCfg.GetModelsForTask(entry.Task).Any(id => string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<string> stemNames = entry.OutputStemNames != null && entry.OutputStemNames.Length > 0
                ? entry.OutputStemNames
                : NeuralModelCatalog.GetDefaultOutputStemNames(entry.Task);
            string stemLine = string.Format(S["AiPickerStemMapFmt"], string.Join(" · ", stemNames));
            string statusTag = installed ? S["NeuralModelInstalledTag"] : S["NeuralModelDownloadBtn"];

            LinearLayout tile = BuildAiPickerOptionTile(
                string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.0} MB)", entry.DisplayName, entry.ApproxSizeMb),
                $"{stemLine} · {statusTag}",
                isSelected,
                installed,
                () => {
                    if (!installed) {
                        ShowToast(string.Format(S["AiPickerNeedDownloadToast"], entry.DisplayName));
                        dialog?.Dismiss();
                        ShowNeuralModelsModal();
                        return;
                    }
                    store.NeuralExecutionBackendIndex = (int)NeuralExecutionBackend.OnDevice;
                    store.NeuralOrchestrationModeIndex = (int)NeuralOrchestrationMode.Manual;
                    AssignActiveModelSlot(entry, toggleEnsemble: false);
                    ApplyAndRefreshNeuralAudio();
                    NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.StudioExport | UiStateDomain.Playback | UiStateDomain.Room3D, immediate: true);
                    ShowToast(string.Format(S["AiPickerAppliedToastFmt"], entry.DisplayName, string.Join(", ", stemNames)));
                    dialog?.Dismiss();
                    onApplied?.Invoke();
                });
            content.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        // Section 2: MVSEP Cloud Recipes (1-tap selects Cloud isolation from StudioBundlePreset)
        TextView cloudHdr = Label(S["AiPickerSectionCloud"], 12.2f, OneUiTheme.IconSystemInfo);
        cloudHdr.SetTypeface(null, TypefaceStyle.Bold);
        cloudHdr.SetPadding(Dp(2), Dp(8), Dp(2), Dp(6));
        content.AddView(cloudHdr);

        bool hasMvsepToken = !string.IsNullOrWhiteSpace(store.MvsepApiToken);
        IReadOnlyList<MvsepAlgorithmPreset> cloudPresets = MvsepAlgorithmPreset.Presets;
        for (int i = 0; i < cloudPresets.Count; i++) {
            MvsepAlgorithmPreset preset = cloudPresets[i];
            bool isSelected = isCloudMode && string.Equals(store.MvsepPresetId, preset.Id, StringComparison.OrdinalIgnoreCase);
            string[] stemNames = GetMvsepPresetStemMap(preset);
            string stemLine = string.Format(S["AiPickerStemMapFmt"], string.Join(" · ", stemNames));

            LinearLayout tile = BuildAiPickerOptionTile(
                preset.DisplayName,
                stemLine,
                isSelected,
                hasMvsepToken,
                () => {
                    store.MvsepPresetId = preset.Id;
                    store.NeuralExecutionBackendIndex = (int)NeuralExecutionBackend.MvsepCloud;
                    ApplyAndRefreshNeuralAudio();
                    NotifyUiDomainChanged(UiStateDomain.Neural | UiStateDomain.StudioExport | UiStateDomain.Playback | UiStateDomain.Room3D, immediate: true);
                    dialog?.Dismiss();
                    if (string.IsNullOrWhiteSpace(store.MvsepApiToken)) {
                        ShowMvsepCloudConfigModal();
                    } else {
                        ShowToast(string.Format(S["AiPickerAppliedToastFmt"], preset.DisplayName, string.Join(", ", stemNames)));
                        onApplied?.Invoke();
                    }
                });
            content.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        LinearLayout footer = HRow();
        footer.SetPadding(0, Dp(6), 0, 0);
        Button onnxMgrBtn = PillButton(string.Format(S["NeuralModelsBtn"], OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count), () => {
            dialog?.Dismiss();
            ShowNeuralModelsModal();
        });
        Button mvsepBtn = PillButton(S["MvsepConfigBtn"], () => {
            dialog?.Dismiss();
            ShowMvsepCloudConfigModal();
        });
        onnxMgrBtn.TextSize = 11.2f;
        mvsepBtn.TextSize = 11.2f;
        onnxMgrBtn.SetSingleLine(true);
        mvsepBtn.SetSingleLine(true);
        onnxMgrBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        mvsepBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        footer.AddView(onnxMgrBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        footer.AddView(mvsepBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(footer);

        dialog = OneUiModalDialog.Show(
            this,
            S["AiPickerTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["CloseBtn"]);
    }

    LinearLayout BuildAiPickerOptionTile(string title, string subtitle, bool selected, bool ready, Action onTap) {
        LinearLayout row = HRow();
        row.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        row.Background = selected
            ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 78), 16f, OneUiTheme.PrimaryHigh, 1f)
            : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
        OneUiTheme.AttachRecoil(row, 0.985f);

        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = Label(title, 12.6f, selected ? ColText : OneUiTheme.TextPrimary);
        titleTv.SetTypeface(null, selected ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

        TextView subTv = Label(subtitle, 11f, selected ? OneUiTheme.TextSecondary : (ready ? OneUiTheme.IconSystemInfo : OneUiTheme.PeachText));
        subTv.SetMaxLines(2);
        subTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        col.AddView(titleTv);
        col.AddView(subTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });

        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        if (selected) {
            row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh), new LinearLayout.LayoutParams(Dp(16), Dp(16)));
        }
        row.Click += (_, _) => onTap?.Invoke();
        return row;
    }

    LinearLayout BuildRoomSpeakerXtcQuickRow() {
        roomSpeakerXtcRow = HRow();
        roomSpeakerXtcRow.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
        roomSpeakerXtcRow.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        OneUiTheme.AttachRecoil(roomSpeakerXtcRow, 0.98f);
        roomSpeakerXtcRow.AddView(new OneUiIconView(this, OneUiIconType.Speaker, OneUiTheme.IconSpatial),
            new LinearLayout.LayoutParams(Dp(17), Dp(17)) { RightMargin = Dp(8) });
        roomSpeakerXtcPresetText = Label("", 12.5f, ColText);
        roomSpeakerXtcPresetText.SetIncludeFontPadding(false);
        roomSpeakerXtcPresetText.Typeface = OneUiTheme.GetAppleFontMedium(this);
        roomSpeakerXtcPresetText.SetSingleLine(true);
        roomSpeakerXtcPresetText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomSpeakerXtcRow.AddView(roomSpeakerXtcPresetText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        roomSpeakerXtcRow.AddView(new OneUiIconView(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub), new LinearLayout.LayoutParams(Dp(15), Dp(15)));
        roomSpeakerXtcRow.Click += (_, _) => ShowSpeakerXtcQuickDialog();
        return roomSpeakerXtcRow;
    }

    void SyncRoomBentoOutputUi(
        (LinearLayout view, Action refresh) tileHrtf,
        (LinearLayout view, Action refresh) tileDist,
        (LinearLayout view, Action refresh) tileSpeakerXtc) {
        if (store == null) {
            return;
        }
        bool isSpeaker = IsActiveOutputSpeakerRoute();
        bool showDist = !isSpeaker && Services.BinauralProfileService.HasVariableDistance(store);
        bool changedLayout = (lastDistTileVisible.HasValue && lastDistTileVisible.Value != showDist) ||
                             (lastSpeakerRouteBento.HasValue && lastSpeakerRouteBento.Value != isSpeaker);
        if (changedLayout && bentoRow1 != null && bentoRow1.IsLaidOut) {
            try {
                Android.Transitions.TransitionSet ts = new();
                ts.SetOrdering(Android.Transitions.TransitionOrdering.Together);
                Android.Transitions.ChangeBounds cb = new();
                cb.SetDuration(280);
                Android.Transitions.Fade fd = new();
                fd.SetDuration(200);
                ts.AddTransition(cb);
                ts.AddTransition(fd);
                ts.SetInterpolator(CreateOneUiEase());
                Android.Transitions.TransitionManager.BeginDelayedTransition(bentoRow1, ts);
            } catch { }
        }
        lastDistTileVisible = showDist;
        lastSpeakerRouteBento = isSpeaker;

        tileHrtf.view.Visibility = isSpeaker ? ViewStates.Gone : ViewStates.Visible;
        tileDist.view.Visibility = showDist ? ViewStates.Visible : ViewStates.Gone;
        tileSpeakerXtc.view.Visibility = isSpeaker ? ViewStates.Visible : ViewStates.Gone;

        if (tileHrtf.view.LayoutParameters is LinearLayout.LayoutParams hlp) {
            int targetRight = showDist ? Dp(8) : 0;
            if (hlp.RightMargin != targetRight) {
                hlp.RightMargin = targetRight;
                tileHrtf.view.LayoutParameters = hlp;
            }
        }
        if (isSpeaker) {
            tileSpeakerXtc.refresh();
        } else {
            tileHrtf.refresh();
            if (showDist) {
                tileDist.refresh();
            }
        }
        if (roomProfRowRef != null) {
            roomProfRowRef.Visibility = isSpeaker ? ViewStates.Gone : ViewStates.Visible;
        }
        if (roomSpeakerXtcRow != null) {
            roomSpeakerXtcRow.Visibility = (isSpeaker && store.TransauralXtcEnabled) ? ViewStates.Visible : ViewStates.Gone;
            if (roomSpeakerXtcPresetText != null) {
                int pct = Math.Clamp((int)Math.Round(store.TransauralXtcStrength * 100f), 20, 100);
                roomSpeakerXtcPresetText.Text = $"{S["RoomXtcPresetRowLabel"]}: {GetTransauralPresetDisplayName(store.TransauralXtcPresetIndex)} · {pct}%";
            }
        }
    }

    string GetRoomSignalChainSpatialLabel(out bool isStageActive) {
        if (IsActiveOutputSpeakerRoute()) {
            isStageActive = store?.TransauralXtcEnabled == true;
            if (!isStageActive) {
                return S["SignalChainSpeakerDirect"];
            }
            float halfAngle = playerEngine?.TransauralXtcDiagnostics.HalfAngleDegrees ?? 0f;
            if (halfAngle <= 0f) {
                var preset = (TransauralSpeakerPreset)Math.Clamp(store?.TransauralXtcPresetIndex ?? 0, 0, 4);
                halfAngle = TransauralXtcFilter.ComputeGeometry(preset).HalfAngleDegrees;
            }
            return string.Format(CultureInfo.InvariantCulture, S["SignalChainSpeakerXtcOnFmt"], halfAngle);
        }
        isStageActive = store?.HeadphoneVirtualizer == true;
        return isStageActive
            ? string.Format(S["SignalChainHrtfOnFmt"], Services.BinauralProfileService.GetActiveDistanceShortText(store, S))
            : S["SignalChainHrtfDirect"];
    }

    Button BuildRoomMiniSpatialChip() {
        Button chip = PillButton("", ToggleActiveOutputSpatialization);
        chip.TextSize = 10.2f;
        chip.SetSingleLine(true);
        chip.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        chip.SetMinWidth(0);
        chip.SetMinimumWidth(0);
        chip.SetMinimumHeight(Dp(30));
        chip.SetPadding(Dp(7), Dp(4), Dp(7), Dp(4));
        SyncRoomMiniSpatialChipUi(chip);
        return chip;
    }

    void SyncRoomMiniSpatialChipUi(Button chip) {
        if (chip == null || store == null) {
            return;
        }
        bool isSpeaker = IsActiveOutputSpeakerRoute();
        bool active = isSpeaker ? store.TransauralXtcEnabled : store.HeadphoneVirtualizer;
        chip.Text = isSpeaker
            ? (active ? S["SpatialOutSpeakerXtcOnShort"] : S["SpatialOutSpeakerXtcOffShort"])
            : (active ? S["BentoHrtf"] : S["PlayerDirectShort"]);
        chip.Background = active
            ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 78), OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 185))
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        chip.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
    }
}
