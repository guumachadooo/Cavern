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
    OneUiInlineSelector neuralQuantModeSelector;
    LinearLayout neuralProSection, neuralCacheDetailsSection, karaokeDetailsSection, lgsrDetailsSection;
    OneUiIconView neuralProChevron;
    bool isNeuralProExpanded;
    Switch neuralQnnCtxCacheSwitch;
    TextView neuralSocCacheStatusText;
    Button neuralClearQnnCacheBtn;

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

        neuralModelPickerBtn = PillButton(BuildActiveAiPickerSummaryText(), () => ShowUnifiedAiModelPickerDialog());
        neuralModelPickerBtn.TextSize = 11.4f;
        neuralModelPickerBtn.SetSingleLine(true);
        neuralModelPickerBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        slot2bCard.AddView(neuralModelPickerBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        neuralPlanSummaryText = Label("", 11.5f, OneUiTheme.TextSub);
        neuralPlanSummaryText.SetSingleLine(true);
        neuralPlanSummaryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        neuralPlanSummaryText.SetPadding(Dp(2), 0, Dp(2), Dp(6));
        slot2bCard.AddView(neuralPlanSummaryText);

        AddDivider(slot2bCard);

        neuralExecBackendSelector = new OneUiInlineSelector(this, S["NeuralExecBackendTitle"]);
        string[] backendLabels = [
            S["NeuralExecBackendOnDevice"],
            S["NeuralExecBackendMvsep"],
            S["NeuralExecBackendAutoHybrid"]
        ];
        neuralExecBackendSelector.SetItems(backendLabels, Math.Clamp(store.NeuralExecutionBackendIndex, 0, backendLabels.Length - 1));
        neuralExecBackendSelector.SelectionChanged += pos => {
            store.NeuralExecutionBackendIndex = pos;
            ApplyAndRefreshNeuralAudio();
            if (pos != 0 && string.IsNullOrWhiteSpace(store.MvsepApiToken)) {
                ShowMvsepCloudConfigModal();
            }
        };
        slot2bCard.AddView(WrapControlWithHelp(neuralExecBackendSelector, S["NeuralExecBackendTitle"], S["TipNeuralExecBackend"], bottomMarginDp: 4));

        mvsepStatusText = Label("", 11.4f, OneUiTheme.TextSub);
        mvsepStatusText.SetPadding(Dp(2), 0, Dp(2), Dp(6));
        slot2bCard.AddView(mvsepStatusText);

        mvsepConfigBtn = PillButton(S["MvsepConfigBtn"], ShowMvsepCloudConfigModal);
        slot2bCard.AddView(mvsepConfigBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
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


        AddDivider(slot2bCard);
        spatialKaraokeSwitch = AddSwitchRow(slot2bCard, S["SpatialKaraokeTitle"], store.SpatialKaraokeEnabled, v => {
            store.SpatialKaraokeEnabled = v;
            if (!v && store.KaraokeVocalLevel < 0.99f) {
                // Keep vocal slider coherent when explicitly disabling Karaoke
                store.KaraokeVocalLevel = 1.0f;
                if (karaokeVocalLevelBar != null && karaokeVocalLevelBar.Progress != 100) {
                    karaokeVocalLevelBar.Progress = 100;
                    karaokeVocalLevelBar.Invalidate();
                }
            } else if (v && store.KaraokeVocalLevel >= 0.99f) {
                // Turning Karaoke ON from 100% defaults to 0% vocal so user immediately hears attenuation
                store.KaraokeVocalLevel = 0.0f;
                if (karaokeVocalLevelBar != null && karaokeVocalLevelBar.Progress != 0) {
                    karaokeVocalLevelBar.Progress = 0;
                    karaokeVocalLevelBar.Invalidate();
                }
            }
            ApplyAndRefreshNeuralAudio();
            RefreshLyricsSingDockUi();
            RefreshStudioW12ContextUi();
        }, helpBody: S["TipSpatialKaraoke"]);

        karaokeDetailsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.SpatialKaraokeEnabled ? ViewStates.Visible : ViewStates.Gone
        };

        (karaokeVocalLevelBar, _) = AddSliderRow(
            karaokeDetailsSection,
            S["KaraokeVocalLevelTitle"],
            0,
            100,
            Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100),
            v => {
                ApplyLyricsSingVocalLevel(v);
            },
            "%",
            defaultValue: 100);

        karaokePreserveBackingSwitch = AddSwitchRow(karaokeDetailsSection, S["KaraokePreserveBackingTitle"], store.KaraokePreserveBackingVocals, v => {
            store.KaraokePreserveBackingVocals = v;
            ApplyAndRefreshNeuralAudio();
            RefreshLyricsSingDockUi();
            RefreshStudioW12ContextUi();
        }, helpBody: S["TipKaraokePureInstrumental"]);
        AddDivider(karaokeDetailsSection);

        karaokeLosslessTwinSwitch = AddSwitchRow(karaokeDetailsSection, S["KaraokeLosslessTwinTitle"], store.UseLosslessTwinForKaraoke, v => {
            store.UseLosslessTwinForKaraoke = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipKaraokeLosslessTwin"]);
        
        slot2bCard.AddView(karaokeDetailsSection);
        AddDivider(slot2bCard);
        neuralTrackCacheEnabledSwitch = AddSwitchRow(slot2bCard, S["NeuralTrackCacheAutoTitle"], store.NeuralTrackCacheEnabled, v => {
            store.NeuralTrackCacheEnabled = v;
            if (neuralTrackCache != null) {
                neuralTrackCache.IsEnabled = v;
            }
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipNeuralTrackCache"]);


        neuralCacheDetailsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.NeuralTrackCacheEnabled ? ViewStates.Visible : ViewStates.Gone
        };
        neuralTrackCacheCompSelector = new OneUiInlineSelector(this, S["NeuralTrackCacheCompTitle"]);
        string[] cacheCompLabels = [
            S["NeuralTrackCacheComp16"],
            S["NeuralTrackCacheComp24"],
            S["NeuralTrackCacheCompRaw"]
        ];
        neuralTrackCacheCompSelector.SetItems(cacheCompLabels, Math.Clamp(store.NeuralTrackCacheCompressionIndex, 0, cacheCompLabels.Length - 1));
        neuralTrackCacheCompSelector.SelectionChanged += pos => {
            store.NeuralTrackCacheCompressionIndex = pos;
            if (neuralTrackCache != null) {
                neuralTrackCache.CompressionMode = (Cavernize.Logic.Spatial.NeuralTrackCacheCompressionMode)pos;
            }
            ApplyAndRefreshNeuralAudio();
        };
        neuralCacheDetailsSection.AddView(WrapControlWithHelp(neuralTrackCacheCompSelector, S["NeuralTrackCacheCompTitle"].TrimEnd(':'), S["TipNeuralTrackCacheComp"], bottomMarginDp: 6));

        neuralTrackCacheCapSelector = new OneUiInlineSelector(this, S["NeuralTrackCacheCapTitle"]);
        string[] cacheCapLabels = ["256 MB", "512 MB", "1024 MB", "2048 MB", "4096 MB"];
        neuralTrackCacheCapSelector.SetItems(cacheCapLabels, ResolveNeuralTrackCachePresetIndex(store.NeuralTrackCacheMaxMb));
        neuralTrackCacheCapSelector.SelectionChanged += pos => {
            int mb = NeuralTrackCachePresetsMb[Math.Clamp(pos, 0, NeuralTrackCachePresetsMb.Length - 1)];
            ApplyNeuralTrackCacheMaxMb(mb);
        };
        neuralCacheDetailsSection.AddView(neuralTrackCacheCapSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(4)
        });

        neuralTrackCacheSavingsText = Label("", 11.4f, OneUiTheme.IconSystemInfo);
        neuralTrackCacheSavingsText.SetPadding(Dp(2), 0, Dp(2), Dp(6));
        neuralCacheDetailsSection.AddView(neuralTrackCacheSavingsText);

        neuralBrowseTrackCacheBtn = PillButton(S["NeuralCacheBrowserBtn"], ShowNeuralTrackCacheBrowserModal);
        neuralCacheDetailsSection.AddView(neuralBrowseTrackCacheBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        
        slot2bCard.AddView(neuralCacheDetailsSection);

        AddDivider(slot2bCard);
        LinearLayout neuralProHeader = HRow();
        neuralProHeader.SetPadding(0, Dp(6), 0, Dp(6));
        OneUiTheme.AttachRecoil(neuralProHeader, 0.98f);
        neuralProHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Wrench, OneUiTheme.IconSystemInfo, 26));
        TextView neuralProTitleTv = Label(S["NeuralProLabSectionTitle"], 12.6f, ColText);
        neuralProTitleTv.Typeface = OneUiTheme.GetAppleFontBold(this);
        neuralProTitleTv.SetPadding(Dp(8), 0, Dp(8), 0);
        neuralProHeader.AddView(neuralProTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        neuralProChevron = new OneUiIconView(this, (isNeuralProExpanded || isSettingsProMode) ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown, OneUiTheme.PeachText);
        neuralProHeader.AddView(neuralProChevron, new LinearLayout.LayoutParams(Dp(16), Dp(16)));
        slot2bCard.AddView(neuralProHeader);

        neuralProSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = (isNeuralProExpanded || isSettingsProMode) ? ViewStates.Visible : ViewStates.Gone
        };
        neuralProSection.SetPadding(0, Dp(6), 0, 0);
        neuralProHeader.Click += (_, _) => {
            isNeuralProExpanded = !isNeuralProExpanded;
            SetExpandedWithAnimation(neuralProSection, isNeuralProExpanded);
            if (neuralProChevron != null) {
                neuralProChevron.IconType = isNeuralProExpanded ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
            }
        };
        neuralRuntimeCheckBtn = PillButton(S["NeuralRuntimeCheckBtn"], () => _ = CheckOnnxRuntimeUpdateAsync(silent: false));
        neuralProSection.AddView(neuralRuntimeCheckBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        neuralModelsBtn = PillButton(string.Format(S["NeuralModelsBtn"], 0), ShowNeuralModelsModal);
        neuralProSection.AddView(neuralModelsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8),
            BottomMargin = Dp(6)
        });

        AddDivider(neuralProSection);

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
        neuralProSection.AddView(WrapControlWithHelp(neuralProviderSelector, S["NeuralProviderTitle"].TrimEnd(':'), S["TipNeuralProvider"], bottomMarginDp: 6));

        // Wave E (W53.5 / B12): Mobile ONNX Precision (Auto / FP32 / FP16 / W8A16) & SoC-Keyed QNN Context Binary Cache
        neuralQuantModeSelector = new OneUiInlineSelector(this, S["NeuralQuantModeTitle"]);
        string[] quantLabels = [
            S["NeuralQuantAuto"],
            S["NeuralQuantFp32"],
            S["NeuralQuantFp16"],
            S["NeuralQuantW8A16"]
        ];
        neuralQuantModeSelector.SetItems(quantLabels, Math.Clamp(store.NeuralQuantizationModeIndex, 0, quantLabels.Length - 1));
        neuralQuantModeSelector.SelectionChanged += pos => {
            store.NeuralQuantizationModeIndex = pos;
            if (neuralRuntime != null) {
                neuralRuntime.PreferredQuantizationMode = (Cavernize.Logic.Spatial.NeuralQuantizationMode)Math.Clamp(pos, 0, 3);
            }
            RefreshNeuralAudioUi();
        };
        neuralProSection.AddView(WrapControlWithHelp(neuralQuantModeSelector, S["NeuralQuantModeTitle"].TrimEnd(':'), S["TipNeuralQuantMode"], bottomMarginDp: 4));

        neuralQnnCtxCacheSwitch = AddSwitchRow(neuralProSection, S["NeuralQnnCacheTitle"], store.NeuralQnnContextCacheEnabled, v => {
            store.NeuralQnnContextCacheEnabled = v;
            if (neuralRuntime != null) {
                neuralRuntime.QnnContextCacheEnabled = v;
            }
            RefreshNeuralAudioUi();
        }, helpBody: S["TipNeuralQnnCache"]);

        neuralSocCacheStatusText = Label("", 11.2f, OneUiTheme.TextSub);
        neuralSocCacheStatusText.SetPadding(Dp(2), 0, Dp(2), Dp(4));
        neuralProSection.AddView(neuralSocCacheStatusText);

        neuralClearQnnCacheBtn = PillButton(S["NeuralQnnCacheClearBtn"], () => {
            ConfirmDestructiveAction(
                S["ConfirmDeleteTitle"],
                S["ConfirmClearQnnCacheMsg"],
                () => {
                    _ = Task.Run(() => {
                        int removed = OnnxQnnContextCacheManager.ClearAllCaches(this);
                        if (!uiDestroyed) {
                            RunOnUiThread(() => {
                                ShowToast(string.Format(S["NeuralQnnCacheClearedToast"], removed));
                                RefreshNeuralAudioUi();
                            });
                        }
                    });
                });
        });
        neuralProSection.AddView(neuralClearQnnCacheBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        neuralComputeBudgetSelector = new OneUiInlineSelector(this, S["NeuralComputeBudgetTitle"]);
        string[] computeBudgetLabels = [
            S["NeuralComputeBudgetEco"],
            S["NeuralComputeBudgetBalanced"],
            S["NeuralComputeBudgetFast"]
        ];
        neuralComputeBudgetSelector.SetItems(computeBudgetLabels, Math.Clamp(store.AiComputeBudgetTier, 0, computeBudgetLabels.Length - 1));
        neuralComputeBudgetSelector.SelectionChanged += pos => {
            store.AiComputeBudgetTier = pos;
            RefreshNeuralAudioUi();
        };
        neuralProSection.AddView(WrapControlWithHelp(neuralComputeBudgetSelector, S["NeuralComputeBudgetTitle"], S["TipNeuralComputeBudget"], bottomMarginDp: 8));

        AddDivider(neuralProSection);

        neuralOrchestrationSelector = new OneUiInlineSelector(this, S["NeuralOrchestrationTitle"]);
        string[] orchLabels = [
            S["NeuralOrchestrationAuto"],
            S["NeuralOrchestrationManual"]
        ];
        neuralOrchestrationSelector.SetItems(orchLabels, Math.Clamp(store.NeuralOrchestrationModeIndex, 0, orchLabels.Length - 1));
        neuralOrchestrationSelector.SelectionChanged += pos => {
            if (IsCoordinatorUpdating) return;
            store.NeuralOrchestrationModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
        };
        neuralProSection.AddView(WrapControlWithHelp(neuralOrchestrationSelector, S["NeuralOrchestrationTitle"], S["TipNeuralOrchestration"], bottomMarginDp: 4));

        neuralStudioBundleSelector = new OneUiInlineSelector(this, S["NeuralStudioBundleTitle"]);
        string[] bundleLabels = [
            S["StudioBundleFastSingle"],
            S["StudioBundleVocalEnsemble"],
            S["StudioBundlePureInstrumental"],
            S["StudioBundleFullBand6Stem"],
            S["StudioBundleKaraoke2Stage"]
        ];
        neuralStudioBundleSelector.SetItems(bundleLabels, Math.Clamp(store.NeuralStudioBundleIndex, 0, bundleLabels.Length - 1));
        neuralStudioBundleSelector.SelectionChanged += pos => {
            if (IsCoordinatorUpdating) return;
            store.NeuralStudioBundleIndex = pos;
            ApplyAndRefreshNeuralAudio();
            RefreshStudioW12ContextUi();
        };
        neuralProSection.AddView(WrapControlWithHelp(neuralStudioBundleSelector, S["NeuralStudioBundleTitle"], S["TipNeuralStudioBundle"], bottomMarginDp: 4));

        AddDivider(neuralProSection);

        (neuralOverlapBar, _) = AddSliderRow(
            neuralProSection,
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

        neuralTtaSwitch = AddSwitchRow(neuralProSection, S["NeuralTtaTitle"], store.NeuralTtaEnabled, v => {
            store.NeuralTtaEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipMvsepTta"]);
        AddDivider(neuralProSection);

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
        neuralProSection.AddView(WrapControlWithHelp(neuralEnsembleSelector, S["NeuralEnsembleTitle"].TrimEnd(':'), S["TipNeuralEnsemble"], bottomMarginDp: 6));

        neuralPhaseFixSwitch = AddSwitchRow(neuralProSection, S["NeuralPhaseFixTitle"], store.NeuralPhaseFixEnabled, v => {
            store.NeuralPhaseFixEnabled = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipMvsepPhaseFix"]);
        AddDivider(neuralProSection);

        neuralConservationSwitch = AddSwitchRow(neuralProSection, S["NeuralConservationTitle"], store.NeuralEnergyConservation, v => {
            store.NeuralEnergyConservation = v;
            ApplyAndRefreshNeuralAudio();
        }, helpBody: S["TipNeuralConservation"]);
        AddDivider(neuralProSection);


        slot2bCard.AddView(neuralProSection);
        RegisterSettingsCard(slot2bCard, categoryIndex: 2, isProOnly: false, "ai neural onnx runtime models orchestrator cache mb mvsep cloud api token bundle ensemble roformer instrumental stems karaoke qnn xnnpack webgpu compute headroom budget eco balanced fast thermal cpu");

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
        lgsrDetailsSection = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = store.LgsrEnabled ? ViewStates.Visible : ViewStates.Gone
        };
        AddDivider(lgsrDetailsSection);

        lgsrAutoDiscoverSwitch = AddSwitchRow(lgsrDetailsSection, S["LgsrAutoDiscoverTitle"], store.LgsrAutoDiscoverTwin, v => {
            store.LgsrAutoDiscoverTwin = v;
            ApplyAndRefreshLgsr(rescanTwin: v);
        });
        AddDivider(lgsrDetailsSection);

        (lgsrFusionBar, _) = AddSliderRow(
            lgsrDetailsSection,
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

        lgsrEnvelopeLockSwitch = AddSwitchRow(lgsrDetailsSection, S["LgsrEnvelopeLockTitle"], store.LgsrDynamicEnvelopeLock, v => {
            store.LgsrDynamicEnvelopeLock = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrEnvelopeLock"]);
        AddDivider(lgsrDetailsSection);

        lgsrClippingGuardSwitch = AddSwitchRow(lgsrDetailsSection, S["LgsrClippingGuardTitle"], store.LgsrClippingGuard, v => {
            store.LgsrClippingGuard = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrClippingGuard"]);
        AddDivider(lgsrDetailsSection);

        lgsrBitstreamGuideSwitch = AddSwitchRow(lgsrDetailsSection, S["LgsrBitstreamGuideTitle"], store.LgsrBitstreamGuide, v => {
            store.LgsrBitstreamGuide = v;
            ApplyAndRefreshLgsr(rescanTwin: false);
        }, helpBody: S["TipLgsrBitstreamGuide"]);
        AddDivider(lgsrDetailsSection);

        lgsrRejectFakeSwitch = AddSwitchRow(lgsrDetailsSection, S["LgsrRejectFakeTitle"], store.LgsrRejectFakeLossless, v => {
            store.LgsrRejectFakeLossless = v;
            ApplyAndRefreshLgsr(rescanTwin: true);
        }, helpBody: S["TipLgsrRejectFake"]);
        AddDivider(lgsrDetailsSection);

        lgsrTier3CleanupSwitch = AddSwitchRow(lgsrDetailsSection, S["LgsrTier3CleanupTitle"], store.LgsrTier3ResidualCleanup, v => {
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
        lgsrDetailsSection.AddView(lgsrBtns, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(10)
        });

        slot2cCard.AddView(lgsrDetailsSection);
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
        _ = playerEngine?.ApplyNeuralConfigAsync(store.BuildNeuralAudioConfig());
        SyncNeuralRuntimeDiagnostics();
        RefreshNeuralAudioUi();
    }

    void RefreshNeuralAudioUi() {
        if (neuralCacheDetailsSection != null && store != null) {
            neuralCacheDetailsSection.Visibility = store.NeuralTrackCacheEnabled ? ViewStates.Visible : ViewStates.Gone;
        }
        if (karaokeDetailsSection != null && store != null) {
            karaokeDetailsSection.Visibility = store.SpatialKaraokeEnabled ? ViewStates.Visible : ViewStates.Gone;
        }
        if (mvsepConfigBtn != null && store != null) {
            bool showMvsep = store.NeuralExecutionBackendIndex != 1;
            mvsepConfigBtn.Visibility = showMvsep ? ViewStates.Visible : ViewStates.Gone;
            if (mvsepStatusText != null) mvsepStatusText.Visibility = showMvsep ? ViewStates.Visible : ViewStates.Gone;
        }
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
        if (mvsepStatusText != null && store != null) {
            bool hasToken = !string.IsNullOrWhiteSpace(store.MvsepApiToken);
            Cavernize.Logic.Spatial.MvsepAlgorithmPreset preset = Cavernize.Logic.Spatial.MvsepAlgorithmPreset.FindOrDefault(store.MvsepPresetId);
            mvsepStatusText.Text = hasToken
                ? string.Format(S["MvsepStatusConfiguredFmt"], Cavernize.Logic.Spatial.MvsepCloudClient.MaskToken(store.MvsepApiToken), preset.DisplayName)
                : S["MvsepStatusUnconfigured"];
            mvsepStatusText.SetTextColor(hasToken ? OneUiTheme.IconSystemInfo : OneUiTheme.TextSub);
        }
        if (neuralTrackCacheSavingsText != null && store != null) {
            Cavernize.Logic.Spatial.NeuralTrackCacheStats cs = neuralTrackCache?.GetStats() ?? default;
            double diskMb = cs.TotalBytes / (1024.0 * 1024.0);
            double rawMb = cs.TotalUncompressedBytes / (1024.0 * 1024.0);
            double savedPct = cs.TotalUncompressedBytes > cs.TotalBytes && cs.TotalUncompressedBytes > 0
                ? Math.Clamp((1.0 - (cs.TotalBytes / (double)cs.TotalUncompressedBytes)) * 100.0, 0.0, 99.0)
                : 0.0;
            neuralTrackCacheSavingsText.Text = cs.EntryCount > 0
                ? string.Format(S["NeuralTrackCacheSavingsFmt"], cs.EntryCount, diskMb, savedPct, rawMb)
                : S["NeuralTrackCacheEmptySummary"];
            neuralTrackCacheSavingsText.SetTextColor(cs.EntryCount > 0 ? OneUiTheme.IconSystemInfo : OneUiTheme.TextSub);
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
            if (neuralQuantModeSelector != null) {
                int qIdx = Math.Clamp(store.NeuralQuantizationModeIndex, 0, 3);
                if (neuralQuantModeSelector.SelectedIndex != qIdx) {
                    neuralQuantModeSelector.SetSelection(qIdx, notify: false);
                }
            }
            if (neuralQnnCtxCacheSwitch != null && neuralQnnCtxCacheSwitch.Checked != store.NeuralQnnContextCacheEnabled) {
                neuralQnnCtxCacheSwitch.Checked = store.NeuralQnnContextCacheEnabled;
            }
            if (neuralSocCacheStatusText != null) {
                _ = Task.Run(() => {
                    QnnHardwareCapabilitySnapshot cap = OnnxQnnContextCacheManager.ProbeDeviceQnnCapabilities(this);
                    (int files, long bytes) = OnnxQnnContextCacheManager.GetCacheStats(this);
                    double mb = bytes / (1024.0 * 1024.0);
                    string line = string.Format(
                        CultureInfo.InvariantCulture,
                        S["NeuralQnnCacheStatusFmt"],
                        cap.Summary,
                        files,
                        mb);
                    if (!uiDestroyed) {
                        RunOnUiThread(() => {
                            if (neuralSocCacheStatusText != null) {
                                neuralSocCacheStatusText.Text = line;
                            }
                        });
                    }
                });
            }
            if (neuralExecBackendSelector != null) {
                int ebIdx = Math.Clamp(store.NeuralExecutionBackendIndex, 0, 2);
                if (neuralExecBackendSelector.SelectedIndex != ebIdx) {
                    neuralExecBackendSelector.SetSelection(ebIdx, notify: false);
                }
            }
            if (neuralComputeBudgetSelector != null) {
                int cbIdx = Math.Clamp(store.AiComputeBudgetTier, 0, 2);
                if (neuralComputeBudgetSelector.SelectedIndex != cbIdx) {
                    neuralComputeBudgetSelector.SetSelection(cbIdx, notify: false);
                }
            }
            if (neuralOrchestrationSelector != null) {
                int oIdx = Math.Clamp(store.NeuralOrchestrationModeIndex, 0, 1);
                if (neuralOrchestrationSelector.SelectedIndex != oIdx) {
                    neuralOrchestrationSelector.SetSelection(oIdx, notify: false);
                }
            }
            if (neuralStudioBundleSelector != null) {
                int bIdx = Math.Clamp(store.NeuralStudioBundleIndex, 0, 4);
                if (neuralStudioBundleSelector.SelectedIndex != bIdx) {
                    neuralStudioBundleSelector.SetSelection(bIdx, notify: false);
                }
            }
            if (neuralTrackCacheEnabledSwitch != null && neuralTrackCacheEnabledSwitch.Checked != store.NeuralTrackCacheEnabled) {
                neuralTrackCacheEnabledSwitch.Checked = store.NeuralTrackCacheEnabled;
            }
            if (neuralTrackCacheCompSelector != null) {
                int ccIdx = Math.Clamp(store.NeuralTrackCacheCompressionIndex, 0, 2);
                if (neuralTrackCacheCompSelector.SelectedIndex != ccIdx) {
                    neuralTrackCacheCompSelector.SetSelection(ccIdx, notify: false);
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
        if (neuralModelPickerBtn != null) {
            neuralModelPickerBtn.Text = BuildActiveAiPickerSummaryText();
        }
        if (!IsCoordinatorUpdating) {
            RefreshSettingsCategorySubtitles();
            if (activeSettingsCategory == 2) {
                RefreshSettingsHubVisibility();
            }
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

        OneUiInlineSelector modalOrchSelector = new(this, S["NeuralOrchestrationTitle"]);
        modalOrchSelector.SetItems([S["NeuralOrchestrationAuto"], S["NeuralOrchestrationManual"]], Math.Clamp(store.NeuralOrchestrationModeIndex, 0, 1));
        content.AddView(modalOrchSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        TextView orchHintTv = Label("", 11.4f, OneUiTheme.TextSecondary);
        orchHintTv.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        orchHintTv.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceSubTile, 12f, OneUiTheme.BorderSubtle, 0.8f);
        content.AddView(orchHintTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
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
            bool isManualEnsemble = currentCfg.OrchestrationMode == Cavernize.Logic.Spatial.NeuralOrchestrationMode.Manual;
            if (modalOrchSelector.SelectedIndex != store.NeuralOrchestrationModeIndex) {
                modalOrchSelector.SetSelection(Math.Clamp(store.NeuralOrchestrationModeIndex, 0, 1), notify: false);
            }
            orchHintTv.Text = isManualEnsemble ? S["NeuralEnsembleCustomHint"] : S["NeuralOrchestrationAutoLockedHint"];
            orchHintTv.SetTextColor(isManualEnsemble ? OneUiTheme.PrimaryHigh : OneUiTheme.TextSecondary);

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

            HashSet<string> autoSelectedIds = new(StringComparer.OrdinalIgnoreCase);
            if (neuralOrchestrator != null) {
                Cavernize.Logic.Spatial.NeuralExecutionPlan plan =
                    neuralOrchestrator.BuildPlan(Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation, currentCfg);
                if (planTv != null) {
                    planTv.Text = string.Format(S["NeuralPlanPreviewFmt"], plan.Summary);
                }
                if (!isManualEnsemble) {
                    Cavernize.Logic.Spatial.NeuralPipelineGoal[] previewGoals = [
                        Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation,
                        Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke,
                        Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna,
                        Cavernize.Logic.Spatial.NeuralPipelineGoal.LosslessResidualRestore
                    ];
                    foreach (Cavernize.Logic.Spatial.NeuralPipelineGoal g in previewGoals) {
                        Cavernize.Logic.Spatial.NeuralExecutionPlan gp = neuralOrchestrator.BuildPlan(g, currentCfg);
                        foreach (Cavernize.Logic.Spatial.NeuralExecutionStep st in gp.Steps) {
                            foreach (Cavernize.Logic.Spatial.NeuralModelCatalogEntry m in st.Models) {
                                autoSelectedIds.Add(m.Id);
                            }
                        }
                    }
                }
            }

            modelsContainer.RemoveAllViews();
            List<Cavernize.Logic.Spatial.NeuralModelCatalogEntry> allModels = OnnxRuntimeInstallerService.GetAllRegisteredModels(this);

            (string GroupTitleKey, Cavernize.Logic.Spatial.NeuralModelTask[] Tasks)[] modelGroups = [
                ("NeuralGroupMultiStem", [Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation6, Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation4]),
                ("NeuralGroupVocals", [Cavernize.Logic.Spatial.NeuralModelTask.VocalSeparation]),
                ("NeuralGroupKaraoke", [Cavernize.Logic.Spatial.NeuralModelTask.KaraokeLeadBacking]),
                ("NeuralGroupDereverb", [Cavernize.Logic.Spatial.NeuralModelTask.Dereverb]),
                ("NeuralGroupDebleed", [Cavernize.Logic.Spatial.NeuralModelTask.DebleedDenoise]),
                ("NeuralGroupRestore", [Cavernize.Logic.Spatial.NeuralModelTask.CodecRestoreApollo, Cavernize.Logic.Spatial.NeuralModelTask.BandwidthExtension])
            ];

            for (int gIdx = 0; gIdx < modelGroups.Length; gIdx++) {
                var grp = modelGroups[gIdx];
                List<Cavernize.Logic.Spatial.NeuralModelCatalogEntry> groupEntries = allModels
                    .Where(m => Array.IndexOf(grp.Tasks, m.Task) >= 0)
                    .ToList();
                if (groupEntries.Count == 0) {
                    continue;
                }

                int activeInGroup = 0;
                foreach (Cavernize.Logic.Spatial.NeuralModelCatalogEntry ge in groupEntries) {
                    if (!OnnxRuntimeInstallerService.IsModelInstalled(this, ge.Id)) {
                        continue;
                    }
                    bool chosen = isManualEnsemble
                        ? currentCfg.GetModelsForTask(ge.Task).Any(id => string.Equals(id, ge.Id, StringComparison.OrdinalIgnoreCase))
                        : autoSelectedIds.Contains(ge.Id);
                    if (chosen) {
                        activeInGroup++;
                    }
                }

                LinearLayout grpHeader = HRow();
                grpHeader.SetPadding(Dp(2), gIdx == 0 ? Dp(2) : Dp(8), Dp(2), Dp(5));
                TextView grpTitle = Label(S[grp.GroupTitleKey], 12.2f, OneUiTheme.TextPrimary);
                grpTitle.SetTypeface(null, TypefaceStyle.Bold);
                grpHeader.AddView(grpTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                string countBadge = activeInGroup > 0
                    ? string.Format(S["NeuralGroupActiveCountFmt"], activeInGroup)
                    : S["NeuralGroupNoneActive"];
                TextView grpCount = Label(countBadge, 11f, activeInGroup > 0 ? OneUiTheme.FunctionalGreen : OneUiTheme.TextSub);
                grpHeader.AddView(grpCount);
                modelsContainer.AddView(grpHeader);

                foreach (Cavernize.Logic.Spatial.NeuralModelCatalogEntry entry in groupEntries) {
                    Cavernize.Logic.Spatial.NeuralModelCatalogEntry localEntry = entry;
                    bool isInst = OnnxRuntimeInstallerService.IsModelInstalled(this, localEntry.Id);
                    IReadOnlyList<string> taskSlots = currentCfg.GetModelsForTask(localEntry.Task);
                    bool inTaskSlot = taskSlots.Any(id => string.Equals(id, localEntry.Id, StringComparison.OrdinalIgnoreCase));
                    bool isAutoChosen = !isManualEnsemble && isInst && autoSelectedIds.Contains(localEntry.Id);

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
                    TextView subTv = Label($"{stemsTag} [{statusHint}]", 11.5f, isInst ? OneUiTheme.IconSystemInfo : OneUiTheme.TextSub);
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
                        if (isManualEnsemble) {
                            actionButtons.Add(PillButton(inTaskSlot ? S["NeuralModelEnsembleInTag"] : S["NeuralModelEnsembleAddBtn"], () => {
                                AssignActiveModelSlot(localEntry, toggleEnsemble: true);
                                ApplyAndRefreshNeuralAudio();
                                RefreshModalInPlace();
                            }, active: inTaskSlot));
                        } else {
                            Button autoBadgeBtn = PillButton(
                                isAutoChosen ? S["NeuralModelAutoActiveTag"] : S["NeuralModelAutoLockedTag"],
                                () => ShowToast(S["NeuralOrchestrationAutoLockedHint"]),
                                active: isAutoChosen);
                            autoBadgeBtn.Alpha = isAutoChosen ? 0.92f : 0.62f;
                            actionButtons.Add(autoBadgeBtn);
                        }

                        actionButtons.Add(PillButton(S["NeuralModelDeleteBtn"], () => {
                            ConfirmDestructiveAction(
                                S["ConfirmDeleteTitle"],
                                string.Format(S["ConfirmDeleteModelMsgFmt"], localEntry.DisplayName),
                                () => {
                                    OnnxRuntimeInstallerService.DeleteModel(this, localEntry.Id);
                                    if (inTaskSlot) {
                                        store.ToggleNeuralTaskModelId(localEntry.Task, localEntry.Id);
                                    }
                                    SyncNeuralRuntimeDiagnostics();
                                    RefreshNeuralAudioUi();
                                    RefreshModalInPlace();
                                });
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
            }

            if (preserveScroll && hostScroll != null) {
                hostScroll.Post(() => {
                    if (!uiDestroyed && !modalClosed) {
                        hostScroll.ScrollTo(0, savedScrollY);
                    }
                });
            }
        }

        modalOrchSelector.SelectionChanged += pos => {
            store.NeuralOrchestrationModeIndex = pos;
            ApplyAndRefreshNeuralAudio();
            RefreshModalInPlace();
        };

        RefreshModalInPlace(preserveScroll: false);

        LinearLayout trackCacheBtnRow = HRow();
        Button browseTracksBtn = PillButton(S["NeuralCacheBrowserBtn"], () => {
            dialog?.Dismiss();
            ShowNeuralTrackCacheBrowserModal();
        });
        Button clearTracksBtn = PillButton(S["NeuralClearTrackCacheBtn"], () => {
            ConfirmDestructiveAction(
                S["ConfirmDeleteTitle"],
                S["ConfirmClearAllTracksMsg"],
                () => {
                    neuralTrackCache?.Clear();
                    SyncNeuralRuntimeDiagnostics();
                    RefreshNeuralAudioUi();
                    ShowToast(S["NeuralTrackCacheClearedToast"]);
                    RefreshModalInPlace();
                });
        });
        trackCacheBtnRow.AddView(browseTracksBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        trackCacheBtnRow.AddView(clearTracksBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(trackCacheBtnRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(6)
        });

        LinearLayout footerRow = HRow();
        Button licBtn = PillButton(S["NeuralLicensesBtn"], () => {
            ShowMessageDialog(S["NeuralLicensesBtn"], Cavernize.Logic.Spatial.NeuralModelCatalog.BuildAttributionNotice(), OneUiIconType.Info);
        });
        Button clearBtn = PillButton(S["NeuralClearCacheBtn"], () => {
            ConfirmDestructiveAction(
                S["ConfirmDeleteTitle"],
                S["ConfirmClearModelsAndCacheMsg"],
                () => {
                    OnnxRuntimeInstallerService.ClearAllModelsAndCache(this);
                    neuralTrackCache?.Clear();
                    SyncNeuralRuntimeDiagnostics();
                    RefreshNeuralAudioUi();
                    ShowToast(S["NeuralCacheClearedToast"]);
                    dialog?.Dismiss();
                });
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
            // Cleanly toggle membership without chain-locking back to a removed primary model ID
            store.ToggleNeuralTaskModelId(entry.Task, entry.Id);
            return;
        }
        store.SetNeuralTaskModelIds(entry.Task, [entry.Id]);
        store.SyncPrimarySlotFromTaskSlots(entry.Task);
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
        if (lgsrDetailsSection != null && store != null) {
            lgsrDetailsSection.Visibility = store.LgsrEnabled ? ViewStates.Visible : ViewStates.Gone;
        }
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

    void ShowMvsepCloudConfigModal() {
        if (uiDestroyed || store == null) {
            return;
        }
        Android.App.Dialog dialog = null;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView descTv = Label(S["MvsepConfigDesc"], 12.2f, OneUiTheme.TextSecondary);
        descTv.SetPadding(0, 0, 0, Dp(10));
        content.AddView(descTv);

        TextView tokenLbl = Label(S["MvsepApiTokenLabel"], 12f, ColText);
        tokenLbl.SetTypeface(null, TypefaceStyle.Bold);
        tokenLbl.SetPadding(0, 0, 0, Dp(4));
        content.AddView(tokenLbl);

        EditText tokenInput = new(this) {
            Hint = S["MvsepApiTokenHint"],
            Text = store.MvsepApiToken,
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword,
            TextSize = 13f
        };
        tokenInput.SetTextColor(ColText);
        tokenInput.SetHintTextColor(OneUiTheme.TextMuted);
        tokenInput.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        tokenInput.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceSubTile, 14f, OneUiTheme.BorderSubtle, 1f);
        content.AddView(tokenInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        string[] serverUrls = [
            Cavernize.Logic.Spatial.MvsepCloudOptions.DefaultBaseUrl,
            Cavernize.Logic.Spatial.MvsepCloudOptions.MirrorBaseUrl
        ];
        string[] serverLabels = ["mvsep.com (Primary)", "de.mvsep.com (EU Mirror)"];
        int serverIdx = string.Equals(store.MvsepServerBaseUrl, serverUrls[1], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        OneUiInlineSelector serverSel = new(this, S["MvsepServerLabel"]);
        serverSel.SetItems(serverLabels, serverIdx);
        content.AddView(serverSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        IReadOnlyList<Cavernize.Logic.Spatial.MvsepAlgorithmPreset> presets = Cavernize.Logic.Spatial.MvsepAlgorithmPreset.Presets;
        string[] presetLabels = presets.Select(p => p.DisplayName).ToArray();
        int presetIdx = 0;
        for (int i = 0; i < presets.Count; i++) {
            if (string.Equals(presets[i].Id, store.MvsepPresetId, StringComparison.OrdinalIgnoreCase)) {
                presetIdx = i;
                break;
            }
        }
        OneUiInlineSelector presetSel = new(this, S["MvsepPresetLabel"]);
        presetSel.SetItems(presetLabels, presetIdx);
        content.AddView(presetSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        Cavernize.Logic.Spatial.MvsepOutputFormat[] formatValues = [
            Cavernize.Logic.Spatial.MvsepOutputFormat.Wav16Bit,
            Cavernize.Logic.Spatial.MvsepOutputFormat.Flac16Bit,
            Cavernize.Logic.Spatial.MvsepOutputFormat.Mp3_320
        ];
        int fmtIdx = Array.IndexOf(formatValues, (Cavernize.Logic.Spatial.MvsepOutputFormat)Math.Clamp(store.MvsepOutputFormatIndex, 0, 2));
        if (fmtIdx < 0) {
            fmtIdx = 0;
        }
        OneUiInlineSelector fmtSel = new(this, S["MvsepOutputFmtLabel"]);
        fmtSel.SetItems(new[] {
            S["MvsepFmtWav16"],
            S["MvsepFmtFlac"],
            S["MvsepFmtMp3"]
        }, fmtIdx);
        content.AddView(fmtSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView testStatusTv = Label("", 11.6f, OneUiTheme.TextSub);
        testStatusTv.SetPadding(0, 0, 0, Dp(6));
        content.AddView(testStatusTv);

        LinearLayout actionRow = HRow();
        Button testBtn = PillButton(S["MvsepTestConnBtn"], () => {
            string chosenUrl = serverUrls[Math.Clamp(serverSel.SelectedIndex, 0, serverUrls.Length - 1)];
            testStatusTv.Text = S["MvsepTestingStatus"];
            testStatusTv.SetTextColor(OneUiTheme.Primary);
            _ = Task.Run(async () => {
                try {
                    using Cavernize.Logic.Spatial.MvsepCloudClient client = new();
                    IReadOnlyList<Cavernize.Logic.Spatial.MvsepRemoteAlgorithmInfo> algos =
                        await client.FetchAlgorithmsAsync(chosenUrl).ConfigureAwait(false);
                    RunOnUiThread(() => {
                        if (uiDestroyed) return;
                        testStatusTv.Text = string.Format(S["MvsepTestOkFmt"], algos.Count);
                        testStatusTv.SetTextColor(OneUiTheme.FunctionalGreen);
                    });
                } catch (Exception ex) {
                    string safe = Cavernize.Logic.Spatial.MvsepCloudClient.RedactSecrets(ex.Message, tokenInput.Text);
                    RunOnUiThread(() => {
                        if (uiDestroyed) return;
                        testStatusTv.Text = string.Format(S["MvsepTestFailFmt"], safe);
                        testStatusTv.SetTextColor(OneUiTheme.FunctionalRed);
                    });
                }
            });
        });
        Button clearTokenBtn = PillButton(S["MvsepClearTokenBtn"], () => {
            ConfirmDestructiveAction(
                S["ConfirmDeleteTitle"],
                S["ConfirmClearMvsepTokenMsg"],
                () => {
                    tokenInput.Text = "";
                    store.MvsepApiToken = "";
                    if (store.NeuralExecutionBackendIndex == (int)Cavernize.Logic.Spatial.NeuralExecutionBackend.MvsepCloud) {
                        store.NeuralExecutionBackendIndex = (int)Cavernize.Logic.Spatial.NeuralExecutionBackend.OnDevice;
                    }
                    ApplyAndRefreshNeuralAudio();
                    testStatusTv.Text = S["MvsepTokenClearedToast"];
                    testStatusTv.SetTextColor(OneUiTheme.TextSub);
                });
        });
        actionRow.AddView(testBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(8) });
        actionRow.AddView(clearTokenBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.85f));
        content.AddView(actionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(4)
        });

        dialog = OneUiModalDialog.Show(
            this,
            S["MvsepConfigTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconAtmosUpmix,
            positiveText: S["SaveBtn"],
            onPositive: () => {
                store.MvsepApiToken = (tokenInput.Text ?? "").Trim();
                store.MvsepServerBaseUrl = serverUrls[Math.Clamp(serverSel.SelectedIndex, 0, serverUrls.Length - 1)];
                int pIdx = Math.Clamp(presetSel.SelectedIndex, 0, presets.Count - 1);
                store.MvsepPresetId = presets[pIdx].Id;
                store.MvsepOutputFormatIndex = (int)formatValues[Math.Clamp(fmtSel.SelectedIndex, 0, formatValues.Length - 1)];
                ApplyAndRefreshNeuralAudio();
                ShowToast(S["MvsepConfigSavedToast"]);
            },
            negativeText: S["CancelBtn"]);
    }

    void ShowNeuralTrackCacheBrowserModal() {
        if (uiDestroyed || neuralTrackCache == null || store == null) {
            return;
        }
        Android.App.Dialog dialog = null;
        bool modalClosed = false;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView headerSummaryTv = Label("", 12f, OneUiTheme.IconSystemInfo);
        headerSummaryTv.SetPadding(0, 0, 0, Dp(2));
        content.AddView(headerSummaryTv);

        TextView folderPathTv = Label(neuralTrackCache.CacheDirectoryPath, 10.6f, OneUiTheme.TextMuted);
        folderPathTv.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        folderPathTv.SetPadding(0, 0, 0, Dp(8));
        content.AddView(folderPathTv);

        LinearLayout entriesContainer = new(this) { Orientation = Orientation.Vertical };
        content.AddView(entriesContainer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        void RefreshBrowserInPlace() {
            if (uiDestroyed || modalClosed) {
                return;
            }
            Cavernize.Logic.Spatial.NeuralTrackCacheStats stats = neuralTrackCache.GetStats();
            double diskMb = stats.TotalBytes / (1024.0 * 1024.0);
            double rawMb = stats.TotalUncompressedBytes / (1024.0 * 1024.0);
            double savedPct = stats.TotalUncompressedBytes > stats.TotalBytes && stats.TotalUncompressedBytes > 0
                ? Math.Clamp((1.0 - (stats.TotalBytes / (double)stats.TotalUncompressedBytes)) * 100.0, 0.0, 99.0)
                : 0.0;
            headerSummaryTv.Text = stats.EntryCount > 0
                ? string.Format(S["NeuralCacheBrowserSummaryFmt"], stats.EntryCount, diskMb, store.NeuralTrackCacheMaxMb, savedPct, rawMb)
                : S["NeuralTrackCacheEmptySummary"];

            entriesContainer.RemoveAllViews();
            IReadOnlyList<Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo> entries = neuralTrackCache.ListEntries();
            if (entries.Count == 0) {
                TextView emptyTv = Label(S["NeuralCacheBrowserEmpty"], 12.2f, OneUiTheme.TextSub);
                emptyTv.SetPadding(Dp(10), Dp(14), Dp(10), Dp(14));
                emptyTv.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
                entriesContainer.AddView(emptyTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(8)
                });
                return;
            }

            foreach (Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo entry in entries) {
                Cavernize.Logic.Spatial.NeuralTrackCacheEntryInfo localEntry = entry;
                LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
                tile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
                tile.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

                string headline = !string.IsNullOrWhiteSpace(localEntry.ArtistName)
                    ? $"{localEntry.DisplayTitle} — {localEntry.ArtistName}"
                    : localEntry.DisplayTitle;
                TextView titleTv = Label(headline, 12.5f, ColText);
                titleTv.SetTypeface(null, TypefaceStyle.Bold);
                titleTv.SetSingleLine(true);
                titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                tile.AddView(titleTv);

                string compTag = localEntry.CompressionMode switch {
                    Cavernize.Logic.Spatial.NeuralTrackCacheCompressionMode.Compact16Bit => "CNTC3 16-bit",
                    Cavernize.Logic.Spatial.NeuralTrackCacheCompressionMode.Studio24Bit => "CNTC3 24-bit",
                    _ => $"CNTC{localEntry.FormatVersion} Raw"
                };
                string durTag = localEntry.DurationSeconds > 0.5 ? FormatMmSs(localEntry.DurationSeconds) : "--:--";
                TextView metaTv = Label(
                    $"{localEntry.OriginTag} · {localEntry.StemCount} stems · {durTag} · {compTag}",
                    11.2f,
                    OneUiTheme.PrimaryHigh);
                metaTv.SetPadding(0, Dp(2), 0, Dp(2));
                tile.AddView(metaTv);

                if (!string.IsNullOrWhiteSpace(localEntry.ModelSummary)) {
                    TextView modelTv = Label(localEntry.ModelSummary, 10.8f, OneUiTheme.TextSub);
                    modelTv.SetSingleLine(true);
                    modelTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    tile.AddView(modelTv);
                }

                double entryMb = localEntry.CompressedBytes / (1024.0 * 1024.0);
                double entryRawMb = localEntry.UncompressedBytes / (1024.0 * 1024.0);
                string dateTag = localEntry.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                string sizeLine = localEntry.SavedPercent >= 5.0
                    ? string.Format(S["NeuralCacheEntrySizeSavedFmt"], entryMb, localEntry.SavedPercent, entryRawMb, dateTag)
                    : string.Format(S["NeuralCacheEntrySizeRawFmt"], entryMb, dateTag);

                LinearLayout bottomRow = HRow();
                bottomRow.SetGravity(GravityFlags.CenterVertical);
                TextView sizeTv = Label(sizeLine, 11f, OneUiTheme.TextSecondary);
                bottomRow.AddView(sizeTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

                Button delBtn = PillButton(S["NeuralModelDeleteBtn"], () => {
                    ConfirmDestructiveAction(
                        S["ConfirmDeleteTitle"],
                        string.Format(S["ConfirmDeleteVariantMsgFmt"], headline),
                        () => {
                            if (neuralTrackCache.DeleteEntry(localEntry.CacheKey)) {
                                SyncNeuralRuntimeDiagnostics();
                                RefreshNeuralAudioUi();
                                ShowToast(S["NeuralCacheEntryDeletedToast"]);
                                RefreshBrowserInPlace();
                            }
                        });
                });
                delBtn.TextSize = 11f;
                delBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
                bottomRow.AddView(delBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                tile.AddView(bottomRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(6)
                });

                entriesContainer.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(8)
                });
            }
        }

        RefreshBrowserInPlace();

        Button clearAllBtn = PillButton(S["NeuralClearTrackCacheBtn"], () => {
            ConfirmDestructiveAction(
                S["ConfirmDeleteTitle"],
                S["ConfirmClearAllTracksMsg"],
                () => {
                    neuralTrackCache.Clear();
                    SyncNeuralRuntimeDiagnostics();
                    RefreshNeuralAudioUi();
                    ShowToast(S["NeuralTrackCacheClearedToast"]);
                    RefreshBrowserInPlace();
                });
        });
        content.AddView(clearAllBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4)
        });

        dialog = OneUiModalDialog.Show(
            this,
            S["NeuralCacheBrowserTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"]);
        if (dialog != null) {
            dialog.DismissEvent += (_, _) => modalClosed = true;
        }
    }

    // Slot 1 / Slot 4: W10 Pro-Audio Full-Duplex USB Engine & Focusrite Scarlett 2i2 Helpers
}
