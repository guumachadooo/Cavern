using System.Collections.Specialized;
using System.Globalization;
using System.Numerics;

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.ProAudio;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavern.Waveforms;

using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using VoidX.WPF.FFmpeg;
using VoidX.WPF.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    void BuildSettingsNeuralCards() {
        // On-Device Neural Audio Runtime (ONNX), MVSEP Anti-Artifact & Spatial Karaoke
        LinearLayout slot2bCard = Card();
        LinearLayout slot2bHeader = HRow();
        slot2bHeader.SetPadding(0, 0, 0, Dp(10));
        slot2bHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sparkles, OneUiTheme.IconAtmosUpmix));
        TextView slot2bTitleTv = SectionTitle(S["Slot2BCardTitle"]);
        slot2bTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        slot2bHeader.AddView(slot2bTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        slot2bHeader.AddView(CreateInlineHelpBtn(S["Slot2BCardTitle"], S["TipNeuralRuntime"]), new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        slot2bCard.AddView(slot2bHeader);

        neuralRuntimeStatusText = Label(S["NeuralRuntimeNotInstalled"], 12f, OneUiTheme.TextSub);
        neuralRuntimeStatusText.SetPadding(0, 0, 0, Dp(8));
        slot2bCard.AddView(neuralRuntimeStatusText);

        neuralRuntimeCheckBtn = PillButton(S["NeuralRuntimeCheckBtn"], () => _ = CheckOnnxRuntimeUpdateAsync(silent: false));
        slot2bCard.AddView(neuralRuntimeCheckBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        neuralModelsBtn = PillButton(string.Format(S["NeuralModelsBtn"], 0), ShowNeuralModelsModal);
        slot2bCard.AddView(neuralModelsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8),
            BottomMargin = Dp(6)
        });

        AddDivider(slot2bCard);

        neuralProviderSelector = new OneUiInlineSelector(this, S["NeuralProviderTitle"]);
        string[] providerLabels = [
            S["NeuralProviderAuto"],
            S["NeuralProviderQnn"],
            S["NeuralProviderXnnpack"],
            S["NeuralProviderCpu"],
            S["NeuralProviderWebGpu"]
        ];
        neuralProviderSelector.SetItems(providerLabels, Math.Clamp(store.NeuralProviderIndex, 0, providerLabels.Length - 1));
        neuralProviderSelector.SelectionChanged += pos => {
            store.NeuralProviderIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        slot2bCard.AddView(neuralProviderSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        neuralOrchestrationSelector = new OneUiInlineSelector(this, S["NeuralOrchestrationTitle"]);
        string[] orchLabels = [
            S["NeuralOrchestrationAuto"],
            S["NeuralOrchestrationManual"]
        ];
        neuralOrchestrationSelector.SetItems(orchLabels, Math.Clamp(store.NeuralOrchestrationModeIndex, 0, orchLabels.Length - 1));
        neuralOrchestrationSelector.SelectionChanged += pos => {
            store.NeuralOrchestrationModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        slot2bCard.AddView(WrapControlWithHelp(neuralOrchestrationSelector, S["NeuralOrchestrationTitle"], S["TipNeuralOrchestration"], bottomMarginDp: 4));

        neuralPlanSummaryText = Label("", 11.5f, OneUiTheme.TextSub);
        neuralPlanSummaryText.SetSingleLine(true);
        neuralPlanSummaryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        neuralPlanSummaryText.SetPadding(Dp(2), 0, Dp(2), Dp(6));
        slot2bCard.AddView(neuralPlanSummaryText);

        neuralTrackCacheCapSelector = new OneUiInlineSelector(this, S["NeuralTrackCacheCapTitle"]);
        string[] cacheCapLabels = ["256 MB", "512 MB", "1024 MB", "2048 MB", "4096 MB"];
        neuralTrackCacheCapSelector.SetItems(cacheCapLabels, ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb));
        neuralTrackCacheCapSelector.SelectionChanged += pos => {
            int mb = NeuralTrackCachePresetsMb[Math.Clamp(pos, 0, NeuralTrackCachePresetsMb.Length - 1)];
            ApplyNeuralTrackCacheMaxMb(mb);
        };
        slot2bCard.AddView(neuralTrackCacheCapSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        (neuralOverlapBar, _) = AddSliderRow(
            slot2bCard,
            S["NeuralOverlapTitle"],
            2,
            8,
            Math.Clamp(store.NeuralOverlapFactor, 2, 8),
            v => {
                store.NeuralOverlapFactor = v;
                if (neuralOverlapBar != null) {
                    neuralOverlapBar.ValueText = $"{v}x";
                    neuralOverlapBar.Invalidate();
                }
                ApplyAndRefreshNeuralAudio();
            },
            "x",
            defaultValue: 2,
            helpBody: S["TipMvsepOverlap"]);
        if (neuralOverlapBar != null) {
            neuralOverlapBar.ValueText = $"{store.NeuralOverlapFactor}x";
        }

        neuralTtaSwitch = AddSwitchRow(slot2bCard, S["NeuralTtaTitle"], store.NeuralTtaEnabled, v => {
            store.NeuralTtaEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipMvsepTta"]);
        AddDivider(slot2bCard);

        neuralEnsembleSelector = new OneUiInlineSelector(this, S["NeuralEnsembleTitle"]);
        string[] ensembleLabels = [
            S["NeuralEnsembleAvgWave"],
            S["NeuralEnsembleMinFft"],
            S["NeuralEnsembleAvgFft"]
        ];
        neuralEnsembleSelector.SetItems(ensembleLabels, Math.Clamp(store.NeuralEnsembleModeIndex, 0, ensembleLabels.Length - 1));
        neuralEnsembleSelector.SelectionChanged += pos => {
            store.NeuralEnsembleModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        slot2bCard.AddView(neuralEnsembleSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        neuralPhaseFixSwitch = AddSwitchRow(slot2bCard, S["NeuralPhaseFixTitle"], store.NeuralPhaseFixEnabled, v => {
            store.NeuralPhaseFixEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipMvsepPhaseFix"]);
        AddDivider(slot2bCard);

        neuralConservationSwitch = AddSwitchRow(slot2bCard, S["NeuralConservationTitle"], store.NeuralEnergyConservation, v => {
            store.NeuralEnergyConservation = v;
            ApplyAndRefreshNeuralAudio();
        });
        AddDivider(slot2bCard);

        spatialKaraokeSwitch = AddSwitchRow(slot2bCard, S["SpatialKaraokeTitle"], store.SpatialKaraokeEnabled, v => {
            store.SpatialKaraokeEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipSpatialKaraoke"]);

        (karaokeVocalLevelBar, _) = AddSliderRow(
            slot2bCard,
            S["KaraokeVocalLevelTitle"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100),
            v => {
                store.KaraokeVocalLevel = v * 0.01f;
                playerEngine?.SetKaraokeVocalLevel(store.KaraokeVocalLevel);
                RefreshLyricsSingDockUi();
                RefreshStudioW12ContextUi();
            },
            "%",
            defaultValue: 100);

        karaokePreserveBackingSwitch = AddSwitchRow(slot2bCard, S["KaraokePreserveBackingTitle"], store.KaraokePreserveBackingVocals, v => {
            store.KaraokePreserveBackingVocals = v;
            ApplyAndRefreshNeuralAudio();
            RefreshStudioW12ContextUi();
        });
        AddDivider(slot2bCard);

        karaokeLosslessTwinSwitch = AddSwitchRow(slot2bCard, S["KaraokeLosslessTwinTitle"], store.UseLosslessTwinForKaraoke, v => {
            store.UseLosslessTwinForKaraoke = v;
            ApplyAndRefreshNeuralAudio();
        });
        AddDivider(slot2bCard);

        neuralUpmixModeSelector = new OneUiInlineSelector(this, S["NeuralUpmixModeTitle"]);
        string[] upmixModeLabels = [
            S["NeuralUpmixParametric"],
            S["NeuralUpmixStems3D"],
            S["NeuralUpmixHybrid714"]
        ];
        neuralUpmixModeSelector.SetItems(upmixModeLabels, Math.Clamp(store.NeuralSpatialUpmixModeIndex, 0, upmixModeLabels.Length - 1));
        neuralUpmixModeSelector.SelectionChanged += pos => {
            store.NeuralSpatialUpmixModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
            RefreshNeuralPrepUi();
            if (pos is 1 or 2 &&
                playerEngine?.NeuralStage?.HasPlayableSeparatedStems != true &&
                !isNeuralPrepRunning &&
                GetActiveTrack() != null) {
                PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, silentIfNotReady: true);
            }
        };
        slot2bCard.AddView(WrapControlWithHelp(neuralUpmixModeSelector, S["NeuralUpmixModeTitle"], S["TipNeuralUpmix"], bottomMarginDp: 2));

        RegisterSettingsCard(slot2bCard, categoryIndex: 2, isProOnly: false, "ai neural onnx runtime models orchestrator cache mb mvsep stems karaoke qnn xnnpack");

        // Universal Lossless Twin & Lossless-Guided Atmos Restoration (LGSR)
        LinearLayout slot2cCard = Card();
        LinearLayout slot2cHeader = HRow();
        slot2cHeader.SetPadding(0, 0, 0, Dp(10));
        slot2cHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sliders, OneUiTheme.IconSystemInfo));
        TextView slot2cTitleTv = SectionTitle(S["Slot2CCardTitle"]);
        slot2cTitleTv.SetPadding(Dp(10), 0, Dp(8), 0);
        slot2cHeader.AddView(slot2cTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        slot2cHeader.AddView(CreateInlineHelpBtn(S["Slot2CCardTitle"], S["TipSlot2CLgsr"]), new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        slot2cCard.AddView(slot2cHeader);

        lgsrStatusText = Label(S["LgsrStatusNone"], 12f, OneUiTheme.TextSub);
        lgsrStatusText.SetPadding(0, 0, 0, Dp(8));
        slot2cCard.AddView(lgsrStatusText);

        lgsrEnabledSwitch = AddSwitchRow(slot2cCard, S["LgsrEnabledTitle"], store.LgsrEnabled, v => {
            store.LgsrEnabled = v;
            ApplyAndRefreshLgsr(rescanTwin: v);
        });
        AddDivider(slot2cCard);

        lgsrAutoDiscoverSwitch = AddSwitchRow(slot2cCard, S["LgsrAutoDiscoverTitle"], store.LgsrAutoDiscoverTwin, v => {
            store.LgsrAutoDiscoverTwin = v;
            ApplyAndRefreshLgsr(rescanTwin: v);
        });
        AddDivider(slot2cCard);

        (lgsrFusionBar, _) = AddSliderRow(
            slot2cCard,
            S["LgsrFusionStrengthTitle"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.LgsrFusionStrength * 100f), 0, 100),
            v => {
                store.LgsrFusionStrength = v * 0.01f;
                ApplyAndRefreshLgsr(rescanTwin: false);
            },
            "%",
            defaultValue: 85);

        lgsrEnvelopeLockSwitch = AddSwitchRow(slot2cCard, S["LgsrEnvelopeLockTitle"], store.LgsrDynamicEnvelopeLock, v => {
            store.LgsrDynamicEnvelopeLock = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrEnvelopeLock"]);
        AddDivider(slot2cCard);

        lgsrClippingGuardSwitch = AddSwitchRow(slot2cCard, S["LgsrClippingGuardTitle"], store.LgsrClippingGuard, v => {
            store.LgsrClippingGuard = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrClippingGuard"]);
        AddDivider(slot2cCard);

        lgsrBitstreamGuideSwitch = AddSwitchRow(slot2cCard, S["LgsrBitstreamGuideTitle"], store.LgsrBitstreamGuide, v => {
            store.LgsrBitstreamGuide = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrBitstreamGuide"]);
        AddDivider(slot2cCard);

        lgsrRejectFakeSwitch = AddSwitchRow(slot2cCard, S["LgsrRejectFakeTitle"], store.LgsrRejectFakeLossless, v => {
            store.LgsrRejectFakeLossless = v;
            ApplyAndRefreshLgsr(rescanTwin: true);
        });
        AddDivider(slot2cCard);

        lgsrTier3CleanupSwitch = AddSwitchRow(slot2cCard, S["LgsrTier3CleanupTitle"], store.LgsrTier3ResidualCleanup, v => {
            store.LgsrTier3ResidualCleanup = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrTier3Cleanup"]);

        LinearLayout lgsrBtns = HRow();
        lgsrAbBypassBtn = PillButton(S["LgsrAbToggleBtn"], () => {
            if (playerEngine != null) {
                playerEngine.LgsrBypassed = !playerEngine.LgsrBypassed;
                RefreshLgsrUi();
                RefreshSpatialXRayHud();
            }
        });
        Button lgsrRescanBtn = PillButton(S["LgsrRescanTwinBtn"], () => {
            CavernizeTrack active = GetActiveTrack();
            if (active != null) {
                StartLosslessTwinDiscoveryAsync(active, forceRescan: true);
            } else {
                RefreshLgsrUi();
            }
        });
        Button lgsrLicBtn = PillButton(S["LgsrLicensesBtn"], ShowLgsrLicensesModal);
        lgsrBtns.AddView(lgsrAbBypassBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(6) });
        lgsrBtns.AddView(lgsrRescanBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(6) });
        lgsrBtns.AddView(lgsrLicBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.90f));
        slot2cCard.AddView(lgsrBtns, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(10)
        });

        RegisterSettingsCard(slot2cCard, categoryIndex: 2, isProOnly: true, "lgsr lossless twin restoration atmos eac3 flac fusion envelope lock tier3");

    }

    static int ResolveNeuralTrackCachePresetIndex(int maxMb) {
        int bestIdx = 1;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < NeuralTrackCachePresetsMb.Length; i++) {
            int diff = Math.Abs(maxMb - NeuralTrackCachePresetsMb[i]);
            if (diff < bestDiff) {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    void ApplyNeuralTrackCacheMaxMb(int mb) {
        int clamped = Math.Clamp(mb, 256, 4096);
        store.NeuralTrackCacheMaxMb = clamped;
        neuralTrackCache?.EvictToLimit((long)clamped * 1024L * 1024L);
        SyncNeuralRuntimeDiagnostics();
        RefreshNeuralAudioUi();
        RefreshSettingsCategorySubtitles();
    }

    void SyncNeuralRuntimeDiagnostics() {
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
        bool qnn = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(this) &&
                   OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        int modelCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
        long totalBytes = OnnxRuntimeInstallerService.GetModelsAndRuntimeSizeBytes(this);
        Cavernize.Logic.Spatial.NeuralTrackCacheStats cacheStats = neuralTrackCache?.GetStats() ?? default;
        string planSummary = neuralOrchestrator != null
            ? neuralOrchestrator.BuildPlan(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, store.BuildNeuralAudioConfig()).Summary
            : string.Empty;
        Cavernize.Logic.Spatial.NeuralExecutionProvider prefEp =
            (Cavernize.Logic.Spatial.NeuralExecutionProvider)Math.Clamp(store.NeuralProviderIndex, 0, 4);
        string epName = !installed
            ? "Not Installed"
            : (neuralRuntime != null
                ? neuralRuntime.DescribeEffectiveProvider(prefEp)
                : "CPU (ARM64 KleidiAI)");
        bool qnnActive = qnn && (neuralRuntime == null || neuralRuntime.SupportsQnnHtp);
        playerEngine?.NeuralStage.UpdateRuntimeDiagnostics(
            installed,
            store.InstalledOnnxRuntimeVersion,
            epName,
            qnnActive,
            modelCount,
            totalBytes,
            trackCacheEntryCount: cacheStats.EntryCount,
            trackCacheSizeBytes: cacheStats.TotalBytes,
            orchestrationSummary: planSummary);
        if (playerEngine != null) {
            playerEngine.SceneHub.NeuralDiagnostics = playerEngine.NeuralStage.BuildDiagnosticsSnapshot();
        }
    }

    void ApplyAndRefreshNeuralAudio() {
        playerEngine?.ApplyNeuralConfig(store.BuildNeuralAudioConfig());
        SyncNeuralRuntimeDiagnostics();
        RefreshNeuralAudioUi();
    }

    void RefreshNeuralAudioUi() {
        if (uiDestroyed) {
            return;
        }
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(this);
        int modelCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
        double cacheMb = OnnxRuntimeInstallerService.GetModelsAndRuntimeSizeBytes(this) / (1024.0 * 1024.0);
        string epName = playerEngine?.NeuralDiagnostics.ActiveProviderName ?? (installed ? "CPU (ARM64 KleidiAI)" : "Not Installed");

        if (neuralModelsBtn != null) {
            neuralModelsBtn.Text = string.Format(S["NeuralModelsBtn"], modelCount);
        }
        if (neuralRuntimeStatusText != null) {
            if (!installed) {
                neuralRuntimeStatusText.Text = S["NeuralRuntimeNotInstalled"];
                neuralRuntimeStatusText.SetTextColor(OneUiTheme.TextSub);
            } else {
                string ver = string.IsNullOrEmpty(store.InstalledOnnxRuntimeVersion) ? "local" : "v" + store.InstalledOnnxRuntimeVersion;
                neuralRuntimeStatusText.Text = string.Format(S["NeuralRuntimeInstalled"], ver, epName, modelCount, cacheMb);
                neuralRuntimeStatusText.SetTextColor(OneUiTheme.IconSystemInfo);
            }
        }
        if (neuralPlanSummaryText != null) {
            string summary = playerEngine?.NeuralDiagnostics.OrchestrationSummary ?? "";
            Cavernize.Logic.Spatial.NeuralTrackCacheStats cacheStats = neuralTrackCache?.GetStats() ?? default;
            double trackCacheMb = cacheStats.TotalBytes / (1024.0 * 1024.0);
            string planLine = !string.IsNullOrEmpty(summary)
                ? string.Format(S["NeuralPlanPreviewFmt"], summary)
                : "";
            string cacheSuffix = cacheStats.EntryCount > 0 ? $" · {cacheStats.EntryCount} ({trackCacheMb:0.0} MB)" : "";
            neuralPlanSummaryText.Text = planLine + cacheSuffix;
            neuralPlanSummaryText.Visibility = string.IsNullOrEmpty(neuralPlanSummaryText.Text) ? ViewStates.Gone : ViewStates.Visible;
        }
        if (neuralTrackCacheCapSelector != null && store != null) {
            int capIdx = ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb);
            if (neuralTrackCacheCapSelector.SelectedIndex != capIdx) {
                neuralTrackCacheCapSelector.SetSelection(capIdx, notify: false);
            }
        }
        if (store != null) {
            if (neuralProviderSelector != null) {
                int pIdx = Math.Clamp(store.NeuralProviderIndex, 0, 4);
                if (neuralProviderSelector.SelectedIndex != pIdx) {
                    neuralProviderSelector.SetSelection(pIdx, notify: false);
                }
            }
            if (neuralOrchestrationSelector != null) {
                int oIdx = Math.Clamp(store.NeuralOrchestrationModeIndex, 0, 1);
                if (neuralOrchestrationSelector.SelectedIndex != oIdx) {
                    neuralOrchestrationSelector.SetSelection(oIdx, notify: false);
                }
            }
            SyncSlider(neuralOverlapBar, store.NeuralOverlapFactor, 2, 8, "x");
            if (neuralTtaSwitch != null && neuralTtaSwitch.Checked != store.NeuralTtaEnabled) {
                neuralTtaSwitch.Checked = store.NeuralTtaEnabled;
            }
            if (neuralEnsembleSelector != null) {
                int eIdx = Math.Clamp(store.NeuralEnsembleModeIndex, 0, 2);
                if (neuralEnsembleSelector.SelectedIndex != eIdx) {
                    neuralEnsembleSelector.SetSelection(eIdx, notify: false);
                }
            }
            if (neuralPhaseFixSwitch != null && neuralPhaseFixSwitch.Checked != store.NeuralPhaseFixEnabled) {
                neuralPhaseFixSwitch.Checked = store.NeuralPhaseFixEnabled;
            }
            if (neuralConservationSwitch != null && neuralConservationSwitch.Checked != store.NeuralEnergyConservation) {
                neuralConservationSwitch.Checked = store.NeuralEnergyConservation;
            }
            if (spatialKaraokeSwitch != null && spatialKaraokeSwitch.Checked != store.SpatialKaraokeEnabled) {
                spatialKaraokeSwitch.Checked = store.SpatialKaraokeEnabled;
            }
            SyncSlider(karaokeVocalLevelBar, (int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100, "%");
            if (karaokePreserveBackingSwitch != null && karaokePreserveBackingSwitch.Checked != store.KaraokePreserveBackingVocals) {
                karaokePreserveBackingSwitch.Checked = store.KaraokePreserveBackingVocals;
            }
            if (karaokeLosslessTwinSwitch != null && karaokeLosslessTwinSwitch.Checked != store.UseLosslessTwinForKaraoke) {
                karaokeLosslessTwinSwitch.Checked = store.UseLosslessTwinForKaraoke;
            }
            if (neuralUpmixModeSelector != null) {
                int uIdx = Math.Clamp(store.NeuralSpatialUpmixModeIndex, 0, 2);
                if (neuralUpmixModeSelector.SelectedIndex != uIdx) {
                    neuralUpmixModeSelector.SetSelection(uIdx, notify: false);
                }
            }
        }
        RefreshSettingsCategorySubtitles();
        if (activeSettingsCategory == 2) {
            RefreshSettingsHubVisibility();
        }
    }

    async Task CheckOnnxRuntimeUpdateAsync(bool silent) {
        try {
            bool preferQnn = store.NeuralProviderIndex == (int)Cavernize.Logic.Spatial.NeuralExecutionProvider.QnnHtp ||
                             store.NeuralProviderIndex == (int)Cavernize.Logic.Spatial.NeuralExecutionProvider.Auto;
            OnnxRuntimeStatusResult status = await OnnxRuntimeInstallerService.CheckStatusAsync(this, store, preferQnn);
            if (status.IsInstalled && neuralRuntime != null) {
                await neuralRuntime.ProbeCapabilitiesAsync();
            }
            latestOnnxStatus = status;
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                SyncNeuralRuntimeDiagnostics();
                RefreshNeuralAudioUi();
                if (!silent) {
                    if (status.IsInstalled) {
                        ShowToast(string.Format(S["NeuralRuntimeUpToDate"], string.IsNullOrEmpty(status.InstalledVersion) ? "local" : "v" + status.InstalledVersion));
                    } else {
                        ShowToast(S["NeuralRuntimeNotInstalled"]);
                    }
                }
            });
        } catch (Exception ex) {
            AppLogger.Warn($"ONNX Runtime check failed: {ex.Message}");
            if (!silent) {
                RunOnUiThread(() => ShowError(ex.Message));
            }
        }
    }

    void ShowNeuralModelsModal() {
        Android.App.Dialog dialog = null;
        bool modalClosed = false;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView summaryTv = Label("", 12f, OneUiTheme.IconSystemInfo);
        summaryTv.SetPadding(0, 0, 0, Dp(4));
        content.AddView(summaryTv);

        TextView trackCacheTv = Label("", 11.5f, OneUiTheme.TextSub);
        trackCacheTv.SetPadding(0, 0, 0, Dp(4));
        content.AddView(trackCacheTv);

        OneUiInlineSelector modalCacheCapSelector = new(this, S["NeuralTrackCacheCapTitle"]);
        string[] modalCacheCapLabels = ["256 MB", "512 MB", "1024 MB", "2048 MB", "4096 MB"];
        modalCacheCapSelector.SetItems(modalCacheCapLabels, ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb));
        modalCacheCapSelector.SelectionChanged += pos => {
            int mb = NeuralTrackCachePresetsMb[Math.Clamp(pos, 0, NeuralTrackCachePresetsMb.Length - 1)];
            ApplyNeuralTrackCacheMaxMb(mb);
            Cavernize.Logic.Spatial.NeuralTrackCacheStats updatedStats = neuralTrackCache?.GetStats() ?? default;
            double updatedMb = updatedStats.TotalBytes / (1024.0 * 1024.0);
            trackCacheTv.Text = string.Format(S["NeuralTrackCacheSummary"], updatedStats.EntryCount, updatedMb, store.NeuralTrackCacheMaxMb);
        };
        content.AddView(modalCacheCapSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView planTv = null;
        if (neuralOrchestrator != null) {
            planTv = Label("", 11.5f, OneUiTheme.Primary);
            planTv.SetSingleLine(true);
            planTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            planTv.SetPadding(0, 0, 0, Dp(8));
            content.AddView(planTv);
        }

        Button importStandaloneBtn = PrimaryButton(S["NeuralImportStandaloneBtn"], () => {
            dialog?.Dismiss();
            ShowImportCustomOnnxDialog();
        });
        content.AddView(importStandaloneBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        ProgressBar modalProg = new(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Progress = 0,
            Visibility = ViewStates.Gone
        };
        OneUiTheme.StyleProgressBar(this, modalProg);
        content.AddView(modalProg, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(12)) { BottomMargin = Dp(8) });

        TextView modalStatusTv = Label("", 11.5f, OneUiTheme.TextSub);
        modalStatusTv.Visibility = ViewStates.Gone;
        content.AddView(modalStatusTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        CancellationTokenSource activeDownloadCts = null;
        bool downloadInFlight = false;
        Button cancelDlBtn = null;
        cancelDlBtn = PillButton(S["NeuralModelCancelDownloadBtn"], () => {
            try { activeDownloadCts?.Cancel(); } catch { }
            if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
        });
        cancelDlBtn.Visibility = ViewStates.Gone;
        content.AddView(cancelDlBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        LinearLayout modelsContainer = new(this) { Orientation = Orientation.Vertical };
        content.AddView(modelsContainer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        void RefreshModalInPlace(bool preserveScroll = true) {
            if (uiDestroyed || modalClosed || store == null) {
                return;
            }
            ScrollView hostScroll = content.Parent?.Parent as ScrollView;
            int savedScrollY = preserveScroll && hostScroll != null ? hostScroll.ScrollY : 0;

            Cavernize.Logic.Spatial.NeuralAudioConfig currentCfg = store.BuildNeuralAudioConfig();
            int installedCount = OnnxRuntimeInstallerService.GetInstalledModelIds(this).Count;
            double cacheMb = OnnxRuntimeInstallerService.GetModelsAndRuntimeSizeBytes(this) / (1024.0 * 1024.0);
            summaryTv.Text = string.Format(S["NeuralModelsCacheSummary"], cacheMb, installedCount);

            Cavernize.Logic.Spatial.NeuralTrackCacheStats trackCacheStats = neuralTrackCache?.GetStats() ?? default;
            double trackCacheMb = trackCacheStats.TotalBytes / (1024.0 * 1024.0);
            trackCacheTv.Text = string.Format(S["NeuralTrackCacheSummary"], trackCacheStats.EntryCount, trackCacheMb, store.NeuralTrackCacheMaxMb);

            int capIdx = ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb);
            if (modalCacheCapSelector.SelectedIndex != capIdx) {
                modalCacheCapSelector.SetSelection(capIdx, notify: false);
            }

            if (neuralOrchestrator != null && planTv != null) {
                Cavernize.Logic.Spatial.NeuralExecutionPlan plan =
                    neuralOrchestrator.BuildPlan(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, currentCfg);
                planTv.Text = string.Format(S["NeuralPlanPreviewFmt"], plan.Summary);
            }

            modelsContainer.RemoveAllViews();
            List<Cavernize.Logic.Spatial.NeuralModelCatalogEntry> allModels = OnnxRuntimeInstallerService.GetAllRegisteredModels(this);
            foreach (Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry in allModels) {
                Cavernize.Logic.Spatial.NeuralModelCatalogEntry localEntry = entry;
                bool isInst = OnnxRuntimeInstallerService.IsModelInstalled(this, localEntry.Id);
                IReadOnlyList<string> taskSlots = currentCfg.GetModelsForTask(localEntry.Task);
                bool inTaskSlot = taskSlots.Any(id => string.Equals(id, localEntry.Id, StringComparison.OrdinalIgnoreCase));
                bool isPrimary = string.Equals(localEntry.Id, store.ActiveStemModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveKaraokeModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveDereverbModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveDebleedModelId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(localEntry.Id, store.ActiveRestorerModelId, StringComparison.OrdinalIgnoreCase);
                bool isActive = isPrimary || inTaskSlot;

                LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
                tile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
                tile.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

                string originTag = localEntry.IsCustomImported ? S["NeuralModelCustomTag"] : localEntry.License;
                TextView titleTv = Label($"{localEntry.DisplayName} ({localEntry.ApproxSizeMb:0.0} MB · {originTag})", 12.5f, ColText);
                titleTv.SetTypeface(null, TypefaceStyle.Bold);
                titleTv.SetSingleLine(true);
                titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                tile.AddView(titleTv);

                string stemsTag = localEntry.OutputStemNames != null && localEntry.OutputStemNames.Length > 0
                    ? string.Join(", ", localEntry.OutputStemNames)
                    : localEntry.Task.ToString();
                string statusHint = isInst
                    ? S["NeuralModelInstalledTag"]
                    : (localEntry.RequiresManualOnnxImport ? S["NeuralModelManualImportHint"] : localEntry.SourceRepository);
                TextView subTv = Label($"{localEntry.Task} · {stemsTag} [{statusHint}]", 11.5f, isInst ? OneUiTheme.IconSystemInfo : OneUiTheme.TextSub);
                subTv.SetPadding(0, Dp(2), 0, Dp(8));
                tile.AddView(subTv);

                List<Button> actionButtons = [];
                if (!isInst && !string.IsNullOrWhiteSpace(localEntry.DownloadUrl)) {
                    actionButtons.Add(PrimaryButton(S["NeuralModelDownloadBtn"], () => {
                        if (downloadInFlight) {
                            ShowToast(S["NeuralModelDownloadInProgressToast"]);
                            return;
                        }
                        downloadInFlight = true;
                        activeDownloadCts?.Dispose();
                        activeDownloadCts = new CancellationTokenSource();
                        CancellationToken dlToken = activeDownloadCts.Token;
                        modalProg.Progress = 0;
                        modalProg.Visibility = ViewStates.Visible;
                        modalStatusTv.Text = $"{localEntry.DisplayName}...";
                        modalStatusTv.Visibility = ViewStates.Visible;
                        if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Visible;
                        Task.Run(async () => {
                            try {
                                await OnnxRuntimeInstallerService.DownloadModelAsync(this, localEntry, (frac, msg) => RunOnUiThread(() => {
                                    if (uiDestroyed || modalClosed) return;
                                    modalProg.Progress = Math.Clamp((int)Math.Round(frac * 1000.0), 0, 1000);
                                    modalStatusTv.Text = msg;
                                }), dlToken);
                                RunOnUiThread(() => {
                                    downloadInFlight = false;
                                    if (uiDestroyed) return;
                                    SyncNeuralRuntimeDiagnostics();
                                    RefreshNeuralAudioUi();
                                    if (!modalClosed) {
                                        modalProg.Visibility = ViewStates.Gone;
                                        modalStatusTv.Visibility = ViewStates.Gone;
                                        if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
                                        RefreshModalInPlace();
                                    }
                                });
                            } catch (System.OperationCanceledException) {
                                RunOnUiThread(() => {
                                    downloadInFlight = false;
                                    if (uiDestroyed || modalClosed) return;
                                    modalProg.Visibility = ViewStates.Gone;
                                    modalStatusTv.Visibility = ViewStates.Gone;
                                    if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
                                });
                            } catch (Exception ex) {
                                RunOnUiThread(() => {
                                    downloadInFlight = false;
                                    if (uiDestroyed) return;
                                    modalProg.Visibility = ViewStates.Gone;
                                    if (cancelDlBtn != null) cancelDlBtn.Visibility = ViewStates.Gone;
                                    modalStatusTv.Text = ex.Message;
                                    if (!modalClosed) {
                                        ShowModelDownloadErrorDialog(localEntry, ex.Message, onOpenImport: () => {
                                            pendingStandaloneCustomOnnxImport = false;
                                            pendingOnnxImportModelId = localEntry.Id;
                                            pendingCustomOnnxTask = localEntry.Task;
                                            dialog?.Dismiss();
                                            OpenFileWithExplorerForExtensions(S["NeuralModelImportBtn"], [".onnx"], ImportCustomOnnxModelFromPath, ReqLoadOnnxModel);
                                        });
                                    }
                                });
                            }
                        });
                    }));
                } else if (isInst) {
                    actionButtons.Add(PillButton(isActive ? S["NeuralModelActiveTag"] : S["NeuralModelSelectBtn"], () => {
                        AssignActiveModelSlot(localEntry, toggleEnsemble: false);
                        ApplyAndRefreshNeuralAudio();
                        RefreshModalInPlace();
                    }, active: isActive));

                    if (store.NeuralOrchestrationModeIndex == (int)Cavernize.Logic.Spatial.NeuralOrchestrationMode.Manual) {
                        actionButtons.Add(PillButton(inTaskSlot ? S["NeuralModelEnsembleInTag"] : S["NeuralModelEnsembleAddBtn"], () => {
                            AssignActiveModelSlot(localEntry, toggleEnsemble: true);
                            ApplyAndRefreshNeuralAudio();
                            RefreshModalInPlace();
                        }, active: inTaskSlot));
                    }

                    actionButtons.Add(PillButton(S["NeuralModelDeleteBtn"], () => {
                        OnnxRuntimeInstallerService.DeleteModel(this, localEntry.Id);
                        SyncNeuralRuntimeDiagnostics();
                        RefreshNeuralAudioUi();
                        RefreshModalInPlace();
                    }));
                }

                if (!localEntry.IsCustomImported) {
                    actionButtons.Add(PillButton(S["NeuralModelImportBtn"], () => {
                        pendingStandaloneCustomOnnxImport = false;
                        pendingOnnxImportModelId = localEntry.Id;
                        pendingCustomOnnxTask = localEntry.Task;
                        dialog?.Dismiss();
                        OpenFileWithExplorerForExtensions(S["NeuralModelImportBtn"], [".onnx"], ImportCustomOnnxModelFromPath, ReqLoadOnnxModel);
                    }));
                    if (!string.IsNullOrWhiteSpace(localEntry.DownloadUrl)) {
                        actionButtons.Add(PillButton(S["NeuralModelOpenLinkBtn"], () => OpenModelUrlInBrowser(localEntry.DownloadUrl)));
                    }
                }

                // 2-row flex layout so 3-4 action pills never clip horizontally
                for (int idx = 0; idx < actionButtons.Count; idx += 2) {
                    LinearLayout row = HRow();
                    row.BaselineAligned = false;
                    row.SetGravity(GravityFlags.CenterVertical);
                    Button first = actionButtons[idx];
                    first.TextSize = 11.6f;
                    first.SetSingleLine(true);
                    first.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    first.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));

                    bool hasSecond = idx + 1 < actionButtons.Count;
                    row.AddView(first, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                        RightMargin = hasSecond ? Dp(6) : 0
                    });
                    if (hasSecond) {
                        Button second = actionButtons[idx + 1];
                        second.TextSize = 11.6f;
                        second.SetSingleLine(true);
                        second.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                        second.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.AddView(second, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                    }
                    tile.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        TopMargin = idx > 0 ? Dp(6) : 0
                    });
                }

                modelsContainer.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });
            }

            if (preserveScroll && hostScroll != null) {
                hostScroll.Post(() => {
                    if (!uiDestroyed && !modalClosed) {
                        hostScroll.ScrollTo(0, savedScrollY);
                    }
                });
            }
        }

        RefreshModalInPlace(preserveScroll: false);

        Button clearTracksBtn = PillButton(S["NeuralClearTrackCacheBtn"], () => {
            neuralTrackCache?.Clear();
            SyncNeuralRuntimeDiagnostics();
            RefreshNeuralAudioUi();
            ShowToast(S["NeuralTrackCacheClearedToast"]);
            RefreshModalInPlace();
        });
        content.AddView(clearTracksBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(6)
        });

        LinearLayout footerRow = HRow();
        Button licBtn = PillButton(S["NeuralLicensesBtn"], () => {
            ShowMessageDialog(S["NeuralLicensesBtn"], Cavernize.Logic.Spatial.NeuralModelCatalog.BuildAttributionNotice(), OneUiIconType.Info);
        });
        Button clearBtn = PillButton(S["NeuralClearCacheBtn"], () => {
            OnnxRuntimeInstallerService.ClearAllModelsAndCache(this);
            neuralTrackCache?.Clear();
            SyncNeuralRuntimeDiagnostics();
            RefreshNeuralAudioUi();
            ShowToast(S["NeuralCacheClearedToast"]);
            dialog?.Dismiss();
        });
        footerRow.AddView(licBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        footerRow.AddView(clearBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(footerRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2) });

        dialog = OneUiModalDialog.Show(
            this,
            S["NeuralModelsModalTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["CloseBtn"],
            onHelp: () => ShowModalGuidedHelp(S["NeuralModelsHelpTitle"], S["NeuralModelsHelpBody"], OneUiIconType.Sparkles),
            helpContentDesc: S["NeuralModelsHelpTitle"]);
        if (dialog != null) {
            dialog.DismissEvent += (_, _) => {
                modalClosed = true;
                try { activeDownloadCts?.Cancel(); } catch { }
                activeDownloadCts?.Dispose();
                activeDownloadCts = null;
            };
        }
    }

    void OpenModelUrlInBrowser(string url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return;
        }
        try {
            Intent browser = new(Intent.ActionView, Android.Net.Uri.Parse(url.Trim()));
            StartActivity(browser);
        } catch (Exception ex) {
            ShowError(ex.Message);
        }
    }

    void CopyModelUrlToClipboard(string url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return;
        }
        if (GetSystemService(ClipboardService) is ClipboardManager cm) {
            cm.PrimaryClip = ClipData.NewPlainText("ONNX Model URL", url.Trim());
            ShowToast(S["NeuralModelLinkCopiedToast"]);
        }
    }

    void ShowModelDownloadErrorDialog(Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry, string errorDetail, Action onOpenImport) {
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        TextView detailTv = Label(errorDetail ?? "", 12.5f, OneUiTheme.TextSecondary);
        detailTv.SetPadding(0, 0, 0, Dp(10));
        body.AddView(detailTv);

        string url = entry?.DownloadUrl ?? "";
        if (!string.IsNullOrWhiteSpace(url)) {
            TextView urlBox = new(this) {
                Text = url,
                TextSize = 11.5f
            };
            urlBox.SetTextColor(OneUiTheme.PrimaryHigh);
            urlBox.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            urlBox.SetTextIsSelectable(true);
            urlBox.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
            urlBox.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            body.AddView(urlBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(10)
            });

            LinearLayout linkRow = HRow();
            linkRow.BaselineAligned = false;
            linkRow.SetGravity(GravityFlags.CenterVertical);

            Button openBtn = PrimaryButton(S["NeuralModelOpenLinkBtn"], () => OpenModelUrlInBrowser(url));
            openBtn.TextSize = 12f;
            openBtn.SetSingleLine(true);
            openBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

            Button copyBtn = PillButton(S["NeuralModelCopyLinkBtn"], () => CopyModelUrlToClipboard(url));
            copyBtn.TextSize = 12f;
            copyBtn.SetSingleLine(true);
            copyBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;

            linkRow.AddView(openBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
            linkRow.AddView(copyBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            body.AddView(linkRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        OneUiModalDialog.Show(
            this,
            S["NeuralModelDownloadFailedTitle"],
            customContent: body,
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.FunctionalAmber,
            positiveText: S["NeuralModelImportBtn"],
            onPositive: onOpenImport,
            negativeText: S["CloseBtn"]);
    }

    void ShowImportCustomOnnxDialog() {
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        Cavernize.Logic.Spatial.NeuralModelTask[] taskOptions = [
            Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation4,
            Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation6,
            Cavernize.Logic.Spatial.NeuralModelTask.VocalSeparation,
            Cavernize.Logic.Spatial.NeuralModelTask.KaraokeLeadBacking,
            Cavernize.Logic.Spatial.NeuralModelTask.Dereverb,
            Cavernize.Logic.Spatial.NeuralModelTask.DebleedDenoise,
            Cavernize.Logic.Spatial.NeuralModelTask.CodecRestoreApollo,
            Cavernize.Logic.Spatial.NeuralModelTask.BandwidthExtension
        ];
        string[] taskLabels = [
            S["NeuralTaskStem4"],
            S["NeuralTaskStem6"],
            S["NeuralTaskVocal"],
            S["NeuralTaskKaraoke"],
            S["NeuralTaskDereverb"],
            S["NeuralTaskDebleed"],
            S["NeuralTaskRestore"],
            S["NeuralTaskBwe"]
        ];

        int selectedIdx = Array.IndexOf(taskOptions, pendingCustomOnnxTask);
        if (selectedIdx < 0) selectedIdx = 0;

        OneUiInlineSelector taskSelector = new(this, S["NeuralImportTaskTitle"]);
        taskSelector.SetItems(taskLabels, selectedIdx);
        taskSelector.SelectionChanged += pos => {
            if (pos >= 0 && pos < taskOptions.Length) {
                pendingCustomOnnxTask = taskOptions[pos];
            }
        };
        body.AddView(taskSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        OneUiModalDialog.Show(
            this,
            S["NeuralImportStandaloneBtn"],
            customContent: body,
            icon: OneUiIconType.Folder,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["NeuralModelImportBtn"],
            onPositive: () => {
                pendingStandaloneCustomOnnxImport = true;
                pendingOnnxImportModelId = "";
                OpenFileWithExplorerForExtensions(S["NeuralImportStandaloneBtn"], [".onnx"], ImportCustomOnnxModelFromPath, ReqLoadOnnxModel);
            },
            negativeText: S["CancelBtn"]);
    }

    void AssignActiveModelSlot(Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry, bool toggleEnsemble = false) {
        if (entry == null) return;
        if (toggleEnsemble) {
            store.ToggleNeuralTaskModelId(entry.Task, entry.Id);
        } else {
            store.SetNeuralTaskModelIds(entry.Task, [entry.Id]);
        }

        switch (entry.Task) {
            case Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation4:
            case Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation6:
            case Cavernize.Logic.Spatial.NeuralModelTask.VocalSeparation:
                store.ActiveStemModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.KaraokeLeadBacking:
                store.ActiveKaraokeModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.Dereverb:
                store.ActiveDereverbModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.DebleedDenoise:
                store.ActiveDebleedModelId = entry.Id;
                break;
            case Cavernize.Logic.Spatial.NeuralModelTask.CodecRestoreApollo:
            case Cavernize.Logic.Spatial.NeuralModelTask.BandwidthExtension:
                store.ActiveRestorerModelId = entry.Id;
                break;
        }
    }

    void ImportCustomOnnxModelFromPath(string path) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        try {
            Cavernize.Logic.Spatial.NeuralModelCatalogEntry imported;
            if (pendingStandaloneCustomOnnxImport) {
                imported = OnnxRuntimeInstallerService.ImportCustomOnnxModel(
                    this,
                    path,
                    targetModelId: "",
                    customTask: pendingCustomOnnxTask);
            } else {
                string slotId = string.IsNullOrEmpty(pendingOnnxImportModelId) ? store.ActiveStemModelId : pendingOnnxImportModelId;
                imported = OnnxRuntimeInstallerService.ImportCustomOnnxModel(
                    this,
                    path,
                    slotId,
                    customTask: pendingCustomOnnxTask);
            }
            pendingStandaloneCustomOnnxImport = false;
            AssignActiveModelSlot(imported, toggleEnsemble: false);
            ApplyAndRefreshNeuralAudio();
            ShowToast(string.Format(S["NeuralModelImportedToast"], imported?.DisplayName ?? Path.GetFileNameWithoutExtension(path)));
        } catch (Exception ex) {
            pendingStandaloneCustomOnnxImport = false;
            ShowError(ex.Message);
        }
    }

    // Slot 2C: Lossless-Guided Atmos Restoration (LGSR, W09) Helpers
    void ApplyAndRefreshLgsr(bool rescanTwin) {
        playerEngine?.ApplyLgsrConfig(store.BuildLgsrConfig());
        if (rescanTwin) {
            CavernizeTrack active = GetActiveTrack();
            if (active != null) {
                StartLosslessTwinDiscoveryAsync(active, forceRescan: true);
                return;
            }
        }
        RefreshLgsrUi();
    }

    void RefreshLgsrUi() {
        if (uiDestroyed || lgsrStatusText == null) {
            return;
        }
        if (store != null) {
            if (lgsrEnabledSwitch != null && lgsrEnabledSwitch.Checked != store.LgsrEnabled) {
                lgsrEnabledSwitch.Checked = store.LgsrEnabled;
            }
            if (lgsrAutoDiscoverSwitch != null && lgsrAutoDiscoverSwitch.Checked != store.LgsrAutoDiscoverTwin) {
                lgsrAutoDiscoverSwitch.Checked = store.LgsrAutoDiscoverTwin;
            }
            SyncSlider(lgsrFusionBar, (int)Math.Round(store.LgsrFusionStrength * 100f), 0, 100, "%");
            if (lgsrEnvelopeLockSwitch != null && lgsrEnvelopeLockSwitch.Checked != store.LgsrDynamicEnvelopeLock) {
                lgsrEnvelopeLockSwitch.Checked = store.LgsrDynamicEnvelopeLock;
            }
            if (lgsrClippingGuardSwitch != null && lgsrClippingGuardSwitch.Checked != store.LgsrClippingGuard) {
                lgsrClippingGuardSwitch.Checked = store.LgsrClippingGuard;
            }
            if (lgsrBitstreamGuideSwitch != null && lgsrBitstreamGuideSwitch.Checked != store.LgsrBitstreamGuide) {
                lgsrBitstreamGuideSwitch.Checked = store.LgsrBitstreamGuide;
            }
            if (lgsrRejectFakeSwitch != null && lgsrRejectFakeSwitch.Checked != store.LgsrRejectFakeLossless) {
                lgsrRejectFakeSwitch.Checked = store.LgsrRejectFakeLossless;
            }
            if (lgsrTier3CleanupSwitch != null && lgsrTier3CleanupSwitch.Checked != store.LgsrTier3ResidualCleanup) {
                lgsrTier3CleanupSwitch.Checked = store.LgsrTier3ResidualCleanup;
            }
        }
        Cavernize.Logic.Spatial.LgsrDiagnostics diag = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        switch (diag.State) {
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced: {
                string fmt = string.IsNullOrEmpty(diag.TwinFormatSummary) ? "FLAC" : diag.TwinFormatSummary;
                string twin = string.IsNullOrEmpty(diag.TwinFileName) ? "Lossless Twin" : diag.TwinFileName;
                lgsrStatusText.Text = string.Format(
                    S["LgsrStatusSynced"],
                    fmt,
                    twin,
                    diag.SyncOffsetMs,
                    diag.DriftPpm,
                    diag.AverageCoherence * 100f,
                    diag.CouplingStartHz / 1000f,
                    diag.SpxStartHz / 1000f);
                lgsrStatusText.SetTextColor(OneUiTheme.IconSystemInfo);
                break;
            }
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.Searching:
                lgsrStatusText.Text = S["LgsrStatusSearching"];
                lgsrStatusText.SetTextColor(OneUiTheme.Primary);
                break;
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless:
                lgsrStatusText.Text = S["LgsrStatusBypassedLossless"];
                lgsrStatusText.SetTextColor(OneUiTheme.TextSub);
                break;
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedFakeLossless:
                lgsrStatusText.Text = S["LgsrStatusBypassedFake"];
                lgsrStatusText.SetTextColor(OneUiTheme.PeachText);
                break;
            case Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedLowConfidence:
                lgsrStatusText.Text = S["LgsrStatusBypassedLowConf"];
                lgsrStatusText.SetTextColor(OneUiTheme.TextSub);
                break;
            default:
                lgsrStatusText.Text = S["LgsrStatusNone"];
                lgsrStatusText.SetTextColor(OneUiTheme.TextSub);
                break;
        }
        if (lgsrAbBypassBtn != null) {
            bool byp = playerEngine?.LgsrBypassed ?? false;
            bool synced = diag.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
            lgsrAbBypassBtn.Text = $"{S["LgsrAbToggleBtn"]}: {(byp ? S["XRayBypassActive"] : S["XRayActiveLive"])}";
            lgsrAbBypassBtn.Background = synced
                ? (byp
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 68), 14f, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 165), 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 68), 14f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 165), 1f))
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
        }
        UpdateSpatialBadgePillVisuals();
        RefreshStudioW12ContextUi();
    }

    void ShowLgsrLicensesModal() {
        OneUiModalDialog.Show(
            this,
            S["LgsrLicensesTitle"],
            Cavernize.Logic.Spatial.LgsrDiagnostics.BuildAttributionNotice(),
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"]);
    }

    // Slot 1 / Slot 4: W10 Pro-Audio Full-Duplex USB Engine & Focusrite Scarlett 2i2 Helpers
}
