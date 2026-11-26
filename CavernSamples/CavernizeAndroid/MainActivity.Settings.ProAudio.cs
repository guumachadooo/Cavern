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
    OneUiInlineSelector proAudioMicSourceSelector;

    void BuildSettingsProAudioCards() {
        Cavernize.Logic.Spatial.ProAudioMicSource savedMicSrc =
            (Cavernize.Logic.Spatial.ProAudioMicSource)Math.Clamp(store.ProAudioMicSource, 0, 2);
        if (audioDeviceProfileManager != null) {
            audioDeviceProfileManager.PreferredMicSource = savedMicSrc;
        }
        usbProAudioManager?.UpdateMicSource(savedMicSrc);

        // Pro-Audio Full-Duplex USB Engine (Path B libusb UAC2 + Path A AAudio Exclusive + Scarlett 2i2)
        LinearLayout proAudioCard = Card(S["ProAudioCardTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        proAudioEnabledSwitch = AddSwitchRow(proAudioCard, S["ProAudioEnableTitle"], store.ProAudioEnabled, v => {
            store.ProAudioEnabled = v;
            ApplyAndRefreshProAudio();
        }, helpBody: S["TipProAudioEngine"]);

        proAudioStatusText = Label("", 11.4f, OneUiTheme.TextPrimary);
        proAudioStatusText.SetSingleLine(true);
        proAudioStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        proAudioCard.AddView(proAudioStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(2)
        });

        proAudioTelemetryText = Label("", 10.8f, OneUiTheme.TextMuted);
        proAudioTelemetryText.SetSingleLine(false);
        proAudioTelemetryText.SetMaxLines(2);
        proAudioTelemetryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        proAudioCard.AddView(proAudioTelemetryText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        proAudioPathSelector = new OneUiInlineSelector(this, S["ProAudioPathTitle"]);
        string[] proAudioPathLabels = [
            S["ProAudioPathAuto"],
            S["ProAudioPathB"],
            S["ProAudioPathA"],
            S["ProAudioPathLegacy"]
        ];
        proAudioPathSelector.SetItems(proAudioPathLabels, Math.Clamp(store.ProAudioPreferredPathIndex, 0, proAudioPathLabels.Length - 1));
        proAudioPathSelector.SelectionChanged += pos => {
            store.ProAudioPreferredPathIndex = pos;
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(WrapControlWithHelp(proAudioPathSelector, S["ProAudioPathTitle"].TrimEnd(':'), S["TipProAudioPath"], bottomMarginDp: 8));

        proAudioBufferSelector = new OneUiInlineSelector(this, S["ProAudioBufferTitle"]);
        string[] proAudioBufLabels = [S["ProAudioBuf64"], S["ProAudioBuf128"], S["ProAudioBuf256"]];
        int initialBufIdx = store.ProAudioBufferFrames <= 64 ? 0 : (store.ProAudioBufferFrames <= 128 ? 1 : 2);
        proAudioBufferSelector.SetItems(proAudioBufLabels, initialBufIdx);
        proAudioBufferSelector.SelectionChanged += pos => {
            store.ProAudioBufferFrames = pos == 0 ? 64 : (pos == 1 ? 128 : 256);
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(WrapControlWithHelp(proAudioBufferSelector, S["ProAudioBufferTitle"].TrimEnd(':'), S["TipProAudioBuffer"], bottomMarginDp: 8));

        proAudioSrSelector = new OneUiInlineSelector(this, S["ProAudioSampleRateTitle"]);
        string[] proAudioSrLabels = [S["ProAudioSrAuto"], "44.1 kHz", "48 kHz", "96 kHz", "192 kHz"];
        int initialSrIdx = Array.IndexOf(ProAudioSampleRateValues, store.ProAudioLockedSampleRate);
        proAudioSrSelector.SetItems(proAudioSrLabels, initialSrIdx >= 0 ? initialSrIdx : 0);
        proAudioSrSelector.SelectionChanged += pos => {
            store.ProAudioLockedSampleRate = ProAudioSampleRateValues[Math.Clamp(pos, 0, ProAudioSampleRateValues.Length - 1)];
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(proAudioSrSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddDivider(proAudioCard);
        proAudioLiveMicSwitch = AddSwitchRow(proAudioCard, S["ProAudioLiveMicTitle"], store.LiveMicEnabled, v => {
            store.LiveMicEnabled = v;
            if (v) {
                RequestRecordAudioPermissionIfNeeded();
                if (!store.ProAudioEnabled) {
                    store.ProAudioEnabled = true;
                }
            }
            ApplyAndRefreshProAudio();
        }, helpBody: S["TipProAudioLiveMic"]);

        proAudioMicSourceSelector = new OneUiInlineSelector(this, S["ProAudioMicSourceTitle"]);
        string[] micSourceLabels = [
            S["ProAudioMicSrcPhone"],
            S["ProAudioMicSrcUsb"],
            S["ProAudioMicSrcAuto"]
        ];
        proAudioMicSourceSelector.SetItems(micSourceLabels, Math.Clamp(store.ProAudioMicSource, 0, micSourceLabels.Length - 1));
        proAudioMicSourceSelector.SelectionChanged += pos => {
            ApplyMicSourceSelection((Cavernize.Logic.Spatial.ProAudioMicSource)Math.Clamp(pos, 0, 2));
        };
        proAudioCard.AddView(proAudioMicSourceSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(6)
        });

        proAudioMicRouteSelector = new OneUiInlineSelector(this, S["ProAudioMicRouteTitle"]);
        string[] micRouteLabels = [
            S["ProAudioMicOff"],
            S["ProAudioMicCh1"],
            S["ProAudioMicCh2"],
            S["ProAudioMicStereo"]
        ];
        proAudioMicRouteSelector.SetItems(micRouteLabels, Math.Clamp(store.LiveMicRoutingIndex, 0, micRouteLabels.Length - 1));
        proAudioMicRouteSelector.SelectionChanged += pos => {
            store.LiveMicRoutingIndex = pos;
            ApplyAndRefreshProAudio();
        };
        proAudioCard.AddView(proAudioMicRouteSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        (proAudioMicGainBar, _) = AddSliderRow(
            proAudioCard,
            S["ProAudioMicGainTitle"],
            -18,
            24,
            Math.Clamp((int)MathF.Round(store.LiveMicInputGainDb), -18, 24),
            v => {
                store.LiveMicInputGainDb = v;
                ApplyAndRefreshProAudio();
            },
            " dB",
            defaultValue: 0);

        (proAudioMicMonitorBar, _) = AddSliderRow(
            proAudioCard,
            S["ProAudioMicMonitorTitle"],
            0,
            125,
            Math.Clamp((int)MathF.Round(store.LiveMicMonitorLevel * 100f), 0, 125),
            v => {
                store.LiveMicMonitorLevel = v * 0.01f;
                ApplyAndRefreshProAudio();
            },
            "%",
            defaultValue: 85);

        LinearLayout proAudioRow1 = HRow();
        proAudioGrantUsbBtn = PillButton(S["ProAudioGrantUsbBtn"], () => {
            if (usbProAudioManager != null && usbProAudioManager.RequestUsbPermission()) {
                ShowToast(S["ProAudioUsbRequestedToast"]);
            } else {
                ShowToast(S["ProAudioUsbNoDeviceToast"]);
            }
            RefreshProAudioUi();
        }, active: true);
        Button calibrateRttBtn = PillButton(S["ProAudioCalibrateRttBtn"], ShowCalibrateRttModal);
        proAudioRow1.AddView(proAudioGrantUsbBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.05f) { RightMargin = Dp(8) });
        proAudioRow1.AddView(calibrateRttBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.95f));
        proAudioCard.AddView(proAudioRow1, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        LinearLayout proAudioRow2 = HRow();
        proAudioScarlettBtn = PillButton(S["ScarlettControlsBtn"], ShowScarlett2i2ControlsModal);
        proAudioVocalChainBtn = PillButton(S["ProAudioVocalChainBtn"], ShowAutoVocalMixMatchModal, active: true);
        proAudioRow2.AddView(proAudioScarlettBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        proAudioRow2.AddView(proAudioVocalChainBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        proAudioCard.AddView(proAudioRow2, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout proAudioRow3 = HRow();
        Button proAudioCreditsBtn = PillButton(S["ProAudioCreditsBtn"], ShowProAudioCreditsModal);
        proAudioRow3.AddView(proAudioCreditsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        proAudioCard.AddView(proAudioRow3, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        RegisterSettingsCard(proAudioCard, categoryIndex: 3, isProOnly: true, "pro audio usb aaudio libusb uac2 microphone live mic vocal mix match focusrite scarlett 2i2 4i4 rtt");

    }

    void ApplyMicSourceSelection(Cavernize.Logic.Spatial.ProAudioMicSource src, bool showFeedbackToast = false) {
        if (store == null) {
            return;
        }
        int clamped = Math.Clamp((int)src, 0, 2);
        Cavernize.Logic.Spatial.ProAudioMicSource clean = (Cavernize.Logic.Spatial.ProAudioMicSource)clamped;
        store.ProAudioMicSource = clamped;
        if (audioDeviceProfileManager != null) {
            audioDeviceProfileManager.PreferredMicSource = clean;
            if (audioDeviceProfileManager.CurrentDevice != null) {
                audioOutput?.UpdateOutputRoute(audioDeviceProfileManager.CurrentDevice);
            }
        }
        usbProAudioManager?.UpdateMicSource(clean);
        ApplyAndRefreshProAudio();
        if (showFeedbackToast) {
            string label = clean switch {
                Cavernize.Logic.Spatial.ProAudioMicSource.UsbInterfaceMic => S["ProAudioMicSrcUsb"],
                Cavernize.Logic.Spatial.ProAudioMicSource.Auto => S["ProAudioMicSrcAuto"],
                _ => S["ProAudioMicSrcPhone"]
            };
            ShowToast(string.Format(S["ProAudioMicSrcSwitchedToast"], label));
        }
    }

    void ApplyAndRefreshProAudio() {
        if (store == null) {
            return;
        }
        Cavernize.Logic.Spatial.ProAudioDuplexConfig cfg = store.BuildProAudioConfig();
        audioOutput?.ApplyProAudioConfig(cfg);
        if (cfg.Enabled && cfg.LiveMicEnabled && audioOutput != null) {
            audioOutput.Start();
        }
        RefreshProAudioUi();
    }

    void RefreshProAudioUi() {
        if (uiDestroyed || store == null) {
            return;
        }
        if (proAudioEnabledSwitch != null && proAudioEnabledSwitch.Checked != store.ProAudioEnabled) {
            proAudioEnabledSwitch.Checked = store.ProAudioEnabled;
        }
        if (proAudioLiveMicSwitch != null && proAudioLiveMicSwitch.Checked != store.LiveMicEnabled) {
            proAudioLiveMicSwitch.Checked = store.LiveMicEnabled;
        }
        if (proAudioPathSelector != null) {
            int pIdx = Math.Clamp(store.ProAudioPreferredPathIndex, 0, 3);
            if (proAudioPathSelector.SelectedIndex != pIdx) {
                proAudioPathSelector.SetSelection(pIdx, notify: false);
            }
        }
        if (proAudioBufferSelector != null) {
            int bIdx = store.ProAudioBufferFrames <= 64 ? 0 : (store.ProAudioBufferFrames <= 128 ? 1 : 2);
            if (proAudioBufferSelector.SelectedIndex != bIdx) {
                proAudioBufferSelector.SetSelection(bIdx, notify: false);
            }
        }
        if (proAudioSrSelector != null) {
            int srIdx = Array.IndexOf(ProAudioSampleRateValues, store.ProAudioLockedSampleRate);
            int safeSrIdx = srIdx >= 0 ? srIdx : 0;
            if (proAudioSrSelector.SelectedIndex != safeSrIdx) {
                proAudioSrSelector.SetSelection(safeSrIdx, notify: false);
            }
        }
        if (proAudioMicSourceSelector != null) {
            int srcIdx = Math.Clamp(store.ProAudioMicSource, 0, 2);
            if (proAudioMicSourceSelector.SelectedIndex != srcIdx) {
                proAudioMicSourceSelector.SetSelection(srcIdx, notify: false);
            }
        }
        if (proAudioMicRouteSelector != null) {
            int mIdx = Math.Clamp(store.LiveMicRoutingIndex, 0, 3);
            if (proAudioMicRouteSelector.SelectedIndex != mIdx) {
                proAudioMicRouteSelector.SetSelection(mIdx, notify: false);
            }
        }
        SyncSlider(proAudioMicGainBar, (int)MathF.Round(store.LiveMicInputGainDb), -18, 24, " dB");
        SyncSlider(proAudioMicMonitorBar, (int)MathF.Round(store.LiveMicMonitorLevel * 100f), 0, 125, "%");

        Cavernize.Logic.Spatial.ProAudioDuplexDiagnostics diag =
            audioOutput?.ProAudioDiagnostics ?? Cavernize.Logic.Spatial.ProAudioDuplexDiagnostics.Default;
        UsbProAudioDeviceSnapshot usbSnap = usbProAudioManager?.CurrentSnapshot;

        if (proAudioStatusText != null) {
            string backendLabel = diag.ActiveBackend switch {
                Cavernize.Logic.Spatial.ActiveProAudioBackendKind.LibUsbUac2Direct => S["ProAudioBackendPathB"],
                Cavernize.Logic.Spatial.ActiveProAudioBackendKind.AAudioOboeFullDuplex => S["ProAudioBackendPathA"],
                _ => S["ProAudioBackendLegacy"]
            };
            string calTag = diag.IsLatencyCalibrated ? S["ProAudioCalibratedTag"] : string.Empty;
            string nextStatus = string.Format(
                S["ProAudioStatusFormat"],
                backendLabel,
                diag.SampleRate / 1000.0,
                diag.BitDepth,
                diag.BufferFrames,
                diag.RoundTripLatencyMs,
                calTag);
            if (!string.Equals(proAudioStatusText.Text, nextStatus, StringComparison.Ordinal)) {
                proAudioStatusText.Text = nextStatus;
                proAudioStatusText.SetTextColor(
                    diag.ActiveBackend == Cavernize.Logic.Spatial.ActiveProAudioBackendKind.LibUsbUac2Direct
                        ? OneUiTheme.IconSystemInfo
                        : (diag.ActiveBackend == Cavernize.Logic.Spatial.ActiveProAudioBackendKind.AAudioOboeFullDuplex
                            ? OneUiTheme.Primary
                            : OneUiTheme.TextSub));
            }
        }

        if (proAudioTelemetryText != null) {
            string nextTel;
            Color nextCol;
            if (usbSnap != null && usbSnap.IsAttached && !usbSnap.HasPermission) {
                nextTel = string.Format(S["ProAudioUsbWaitingPerm"], usbSnap.DisplayName);
                nextCol = OneUiTheme.PeachText;
            } else if (!store.ProAudioEnabled && (usbSnap == null || !usbSnap.IsAttached)) {
                nextTel = S["ProAudioNoUsbAttached"];
                nextCol = OneUiTheme.TextMuted;
            } else {
                string syncMode = string.IsNullOrEmpty(diag.ClockSyncMode) ? "-" : diag.ClockSyncMode;
                string baseTel = string.Format(
                    S["ProAudioTelemetryFormat"],
                    syncMode,
                    diag.AsyncClockDriftPpm,
                    diag.OutputXRuns,
                    diag.InputXRuns,
                    diag.MicPeakDbfs);
                string inSrc = string.IsNullOrEmpty(diag.ActiveInputSource) ? S["SingDockMicSrcPhoneChip"] : diag.ActiveInputSource;
                string outMode = string.IsNullOrEmpty(diag.HeadphoneOutputMode) ? "-" : diag.HeadphoneOutputMode;
                nextTel = $"{baseTel}\n{string.Format(S["ProAudioRouteTelemetryFmt"], inSrc, outMode)}";
                nextCol = OneUiTheme.TextMuted;
            }
            if (!string.Equals(proAudioTelemetryText.Text, nextTel, StringComparison.Ordinal)) {
                proAudioTelemetryText.Text = nextTel;
                proAudioTelemetryText.SetTextColor(nextCol);
            }
        }
        RefreshLyricsSingDockUi();
    }

    // Slot 2D: W11/W14 Auto-Vocal Mix-Match & Live Mastering Vocal Chain Modal + Persistence Sync
    void ApplyAndRefreshVocalChain() {
        if (store == null || playerEngine == null) {
            return;
        }
        Cavern.VocalChain.VocalMacroSettings macros = store.BuildVocalMacroSettings();
        macros.Bypass = playerEngine.VocalChainBypassed;
        playerEngine.VocalMixMatchStage.ApplyVocalProfile(playerEngine.VocalMixMatchStage.ActiveDna, macros);
        RefreshLyricsSingDockUi();
        RefreshSpatialXRayHud();
    }

    void ShowAutoVocalMixMatchModal() {
        if (store == null || playerEngine == null) {
            return;
        }
        const float VoiceprintDurationSec = 4.0f;
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };

        TextView telLabel = Label("", 11.2f, OneUiTheme.IconSystemInfo);
        telLabel.SetPadding(0, 0, 0, Dp(8));
        content.AddView(telLabel);

        // Song Vocal DNA & Mic Voiceprint readout card (W19.b)
        LinearLayout dnaCard = new(this) { Orientation = Orientation.Vertical };
        dnaCard.Background = OneUiTheme.CreateRoundedDrawable(
            this, OneUiTheme.SurfaceSubTile, 16f, OneUiTheme.BorderSubtle, 0.9f);
        dnaCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        TextView dnaTitleTv = Label(S["VocalDnaSectionTitle"], 12.2f, OneUiTheme.TextPrimary);
        dnaTitleTv.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        dnaCard.AddView(dnaTitleTv);

        TextView dnaSummaryTv = Label("", 11.2f, OneUiTheme.TextSub);
        dnaSummaryTv.SetPadding(0, Dp(4), 0, 0);
        dnaCard.AddView(dnaSummaryTv);

        TextView vpStatusTv = Label("", 11.2f, OneUiTheme.TextMuted);
        vpStatusTv.SetPadding(0, Dp(4), 0, Dp(8));
        dnaCard.AddView(vpStatusTv);

        // Action row 1: [Analyze Song DNA] [Calibrate Mic (4s)]
        LinearLayout dnaActionRow = HRow();
        dnaActionRow.BaselineAligned = false;
        dnaActionRow.SetGravity(GravityFlags.CenterVertical);

        Button analyzeDnaBtn = null;
        Button calibrateMicBtn = null;
        Button resetVpBtn = null;
        Button abCompareBtn = null;
        Switch bypassSwitchRef = null;
        bool dialogClosed = false;

        void RefreshModalLiveState() {
            if (uiDestroyed || dialogClosed || playerEngine == null || store == null) {
                return;
            }
            Cavern.VocalChain.VocalChainTelemetry tel = playerEngine.SceneHub.VocalChainDiagnostics;
            telLabel.Text = string.Format(
                S["VocalLiveTelemetryFormat"],
                tel.InputDbfs,
                tel.OutputDbfs,
                tel.CompressorReductionDb,
                tel.DeEsserReductionDb,
                tel.SidechainPocketDuckDb);

            Cavern.VocalChain.VocalMixDnaProfile dna = playerEngine.VocalMixMatchStage.ActiveDna;
            if (dna.IsExtractedFromSong) {
                string delayTag = dna.DelayTimeMs > 1f && dna.DelayWetMix > 0.005f
                    ? string.Format(S["VocalDnaDelayTagFmt"], dna.DelayTimeMs)
                    : string.Empty;
                string drrTag = dna.HasMeasuredDereverb && float.IsFinite(dna.MeasuredDrrDb)
                    ? string.Format(S["VocalDnaDrrTagFmt"], dna.MeasuredDrrDb)
                    : string.Empty;
                string trackLabel = string.IsNullOrWhiteSpace(dna.SourceTrackSummary)
                    ? S["TabMusicPlayer"]
                    : dna.SourceTrackSummary;
                dnaSummaryTv.Text = string.Format(
                    S["VocalDnaSummaryFmt"],
                    trackLabel,
                    dna.TargetVocalLufs,
                    dna.CrestFactorDb,
                    dna.ReverbRt60Sec,
                    dna.ReverbWetMix * 100f,
                    delayTag,
                    drrTag);
                dnaSummaryTv.SetTextColor(OneUiTheme.FunctionalGreen);
            } else {
                dnaSummaryTv.Text = S["VocalDnaNeutralStatus"];
                dnaSummaryTv.SetTextColor(OneUiTheme.TextSub);
            }

            bool capturing = playerEngine.LiveMicBridge.IsCapturingVoiceprint;
            if (capturing) {
                float progress = playerEngine.LiveMicBridge.VoiceprintCaptureProgress;
                float remSec = Math.Max(0f, VoiceprintDurationSec * (1f - progress));
                float livePeak = audioOutput?.ProAudioDiagnostics.MicPeakDbfs ?? tel.InputDbfs;
                vpStatusTv.Text = string.Format(S["VocalVoiceprintCapturingFmt"], progress * 100f, remSec, livePeak);
                vpStatusTv.SetTextColor(OneUiTheme.FunctionalOrange);
                if (calibrateMicBtn != null) {
                    calibrateMicBtn.Text = string.Format(S["VocalVoiceprintCancelBtn"], remSec);
                    calibrateMicBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.FunctionalOrange, OneUiTheme.PillCornerDp);
                }
            } else {
                bool hasVp = playerEngine.VocalMixMatchStage.HasMicVoiceprint;
                float[] deltas = playerEngine.VocalMixMatchStage.GetActiveMatchEqDeltasDb();
                if (hasVp && deltas != null && deltas.Length >= 8) {
                    float low = (deltas[0] + deltas[1]) * 0.5f;
                    float mid = (deltas[2] + deltas[3] + deltas[4] + deltas[5]) * 0.25f;
                    float air = (deltas[6] + deltas[7]) * 0.5f;
                    vpStatusTv.Text = string.Format(S["VocalVoiceprintActiveFmt"], low, mid, air);
                    vpStatusTv.SetTextColor(OneUiTheme.IconSystemInfo);
                } else if (hasVp) {
                    vpStatusTv.Text = S["VocalVoiceprintWaitingSongDna"];
                    vpStatusTv.SetTextColor(OneUiTheme.IconSystemInfo);
                } else {
                    vpStatusTv.Text = S["VocalVoiceprintUncalibrated"];
                    vpStatusTv.SetTextColor(OneUiTheme.TextMuted);
                }
                if (calibrateMicBtn != null) {
                    calibrateMicBtn.Text = S["VocalVoiceprintCalibrateBtn"];
                    calibrateMicBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                        this,
                        hasVp ? OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 76) : OneUiTheme.SurfaceCard,
                        OneUiTheme.PillCornerDp);
                }
            }

            if (analyzeDnaBtn != null) {
                analyzeDnaBtn.Text = isNeuralPrepRunning
                    ? string.Format(S["VocalDnaBusyBtn"], neuralPrepProgress)
                    : S["VocalDnaAnalyzeBtn"];
                Color dnaBg = isNeuralPrepRunning
                    ? OneUiTheme.FunctionalOrange
                    : (dna.IsExtractedFromSong ? OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 76) : OneUiTheme.SurfaceCard);
                analyzeDnaBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, dnaBg, OneUiTheme.PillCornerDp);
                analyzeDnaBtn.SetTextColor(isNeuralPrepRunning ? OneUiTheme.GetContrastingTextColor(dnaBg) : ColText);
            }

            if (resetVpBtn != null) {
                resetVpBtn.Visibility = playerEngine.VocalMixMatchStage.HasMicVoiceprint && !capturing
                    ? ViewStates.Visible
                    : ViewStates.Gone;
            }

            if (abCompareBtn != null) {
                bool dryBypass = playerEngine.VocalChainBypassed;
                abCompareBtn.Text = dryBypass ? S["VocalAbStateDry"] : S["VocalAbStateWet"];
                Color abBg = dryBypass ? OneUiTheme.FunctionalOrange : OneUiTheme.Primary;
                abCompareBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, abBg, OneUiTheme.PillCornerDp);
                abCompareBtn.SetTextColor(OneUiTheme.GetContrastingTextColor(abBg));
            }
            if (bypassSwitchRef != null && bypassSwitchRef.Checked != playerEngine.VocalChainBypassed) {
                bypassSwitchRef.Checked = playerEngine.VocalChainBypassed;
            }
        }

        analyzeDnaBtn = PillButton(S["VocalDnaAnalyzeBtn"], () => {
            PrepareSongVocalDnaAsync();
            RefreshModalLiveState();
        });
        analyzeDnaBtn.TextSize = 11.4f;
        analyzeDnaBtn.SetSingleLine(false);
        analyzeDnaBtn.SetMaxLines(2);
        analyzeDnaBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        dnaActionRow.AddView(analyzeDnaBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        calibrateMicBtn = PillButton(S["VocalVoiceprintCalibrateBtn"], () => {
            if (playerEngine == null) {
                return;
            }
            if (playerEngine.LiveMicBridge.IsCapturingVoiceprint) {
                playerEngine.CancelMicVoiceprintCalibration();
                RefreshModalLiveState();
                return;
            }
            RequestRecordAudioPermissionIfNeeded();
            if (!store.ProAudioEnabled) {
                store.ProAudioEnabled = true;
            }
            if (!store.LiveMicEnabled) {
                store.LiveMicEnabled = true;
            }
            if (store.LiveMicRoutingIndex == 0) {
                store.LiveMicRoutingIndex = 1; // MonoChannel1
            }
            ApplyAndRefreshProAudio();
            playerEngine?.StartMicVoiceprintCalibration(VoiceprintDurationSec, result => RunOnUiThread(() => {
                if (uiDestroyed || playerEngine == null) {
                    return;
                }
                if (result.IsValid && result.Profile != null) {
                    store.SaveMicVoiceprintBands(result.Profile.BandEnergyDb);
                    Cavern.VocalChain.VocalMacroSettings macros = store.BuildVocalMacroSettings();
                    macros.Bypass = playerEngine.VocalChainBypassed;
                    playerEngine.VocalMixMatchStage.ApplyMicVoiceprint(result.Profile.BandEnergyDb, macros);
                    RefreshLyricsSingDockUi();
                    RefreshSpatialXRayHud();
                    ShowToast(string.Format(
                        S["VocalVoiceprintSuccessToast"],
                        result.ActiveRmsDbfs,
                        result.ActiveWindowCount));
                } else {
                    ShowToast(string.Format(S["VocalVoiceprintTooQuietFmt"], result.ActiveRmsDbfs));
                }
                RefreshModalLiveState();
            }));
            RefreshModalLiveState();
        });
        calibrateMicBtn.TextSize = 11.4f;
        calibrateMicBtn.SetSingleLine(false);
        calibrateMicBtn.SetMaxLines(2);
        calibrateMicBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        dnaActionRow.AddView(calibrateMicBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        dnaCard.AddView(dnaActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Action row 2: [Instant A/B Compare] [Reset Mic]
        LinearLayout abActionRow = HRow();
        abActionRow.BaselineAligned = false;
        abActionRow.SetGravity(GravityFlags.CenterVertical);
        abActionRow.SetPadding(0, Dp(6), 0, 0);

        abCompareBtn = PillButton(S["VocalAbStateWet"], () => {
            if (playerEngine == null) {
                return;
            }
            playerEngine.VocalChainBypassed = !playerEngine.VocalChainBypassed;
            ApplyAndRefreshVocalChain();
            RefreshModalLiveState();
        });
        abCompareBtn.TextSize = 11.6f;
        abCompareBtn.SetSingleLine(false);
        abCompareBtn.SetMaxLines(2);
        abCompareBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        abActionRow.AddView(abCompareBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        resetVpBtn = PillButton(S["VocalVoiceprintResetBtn"], () => {
            if (playerEngine == null) {
                return;
            }
            store.ClearMicVoiceprint();
            Cavern.VocalChain.VocalMacroSettings macros = store.BuildVocalMacroSettings();
            macros.Bypass = playerEngine.VocalChainBypassed;
            playerEngine.VocalMixMatchStage.ClearMicVoiceprint(macros);
            RefreshLyricsSingDockUi();
            RefreshSpatialXRayHud();
            ShowToast(S["VocalVoiceprintResetToast"]);
            RefreshModalLiveState();
        });
        resetVpBtn.TextSize = 11.4f;
        resetVpBtn.SetSingleLine(true);
        resetVpBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        abActionRow.AddView(resetVpBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(6) });
        dnaCard.AddView(abActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        content.AddView(dnaCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(content, S["ProAudioLiveMicTitle"], store.LiveMicEnabled, v => {
            store.LiveMicEnabled = v;
            if (v) {
                RequestRecordAudioPermissionIfNeeded();
                if (!store.ProAudioEnabled) {
                    store.ProAudioEnabled = true;
                }
                if (store.LiveMicRoutingIndex == 0) {
                    store.LiveMicRoutingIndex = 1;
                }
            }
            ApplyAndRefreshProAudio();
            RefreshModalLiveState();
        });
        AddDivider(content);

        AddSwitchRow(content, S["VocalEnableTitle"], store.VocalMixMatchEnabled, v => {
            store.VocalMixMatchEnabled = v;
            ApplyAndRefreshVocalChain();
            RefreshModalLiveState();
        });
        AddDivider(content);

        bypassSwitchRef = AddSwitchRow(content, S["VocalBypassAbTitle"], playerEngine?.VocalChainBypassed ?? false, v => {
            if (playerEngine != null) {
                playerEngine.VocalChainBypassed = v;
            }
            ApplyAndRefreshVocalChain();
            RefreshModalLiveState();
        });
        AddDivider(content);

        AddSliderRow(
            content,
            S["VocalMixMatchPctTitle"],
            0,
            100,
            Math.Clamp((int)MathF.Round(store.VocalMixMatchPct), 0, 100),
            v => {
                store.VocalMixMatchPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 75);

        AddSliderRow(
            content,
            S["VocalReverbPctTitle"],
            0,
            150,
            Math.Clamp((int)MathF.Round(store.VocalReverbPct), 0, 150),
            v => {
                store.VocalReverbPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 100);

        AddSliderRow(
            content,
            S["VocalDynamicsPctTitle"],
            0,
            150,
            Math.Clamp((int)MathF.Round(store.VocalDynamicsPct), 0, 150),
            v => {
                store.VocalDynamicsPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 100);

        AddSliderRow(
            content,
            S["VocalWarmthPctTitle"],
            0,
            100,
            Math.Clamp((int)MathF.Round(store.VocalWarmthPct), 0, 100),
            v => {
                store.VocalWarmthPct = v;
                ApplyAndRefreshVocalChain();
            },
            "%",
            defaultValue: 35);

        AddSliderRow(
            content,
            S["VocalLevelDbTitle"],
            -18,
            12,
            Math.Clamp((int)MathF.Round(store.VocalLevelDb), -18, 12),
            v => {
                store.VocalLevelDb = v;
                ApplyAndRefreshVocalChain();
            },
            " dB",
            defaultValue: 0);

        AddDivider(content);
        AddSwitchRow(content, S["VocalSidechainGlueTitle"], store.VocalSidechainPocketGlue, v => {
            store.VocalSidechainPocketGlue = v;
            ApplyAndRefreshVocalChain();
        });
        AddDivider(content);
        AddSwitchRow(content, S["VocalNoiseGateTitle"], store.VocalNoiseGate, v => {
            store.VocalNoiseGate = v;
            ApplyAndRefreshVocalChain();
        });
        AddDivider(content);
        AddSwitchRow(content, S["VocalDeEsserTitle"], store.VocalDeEsser, v => {
            store.VocalDeEsser = v;
            ApplyAndRefreshVocalChain();
        });

        LinearLayout scarlettRow = HRow();
        scarlettRow.BaselineAligned = false;
        scarlettRow.SetGravity(GravityFlags.CenterVertical);
        scarlettRow.SetPadding(0, Dp(8), 0, 0);
        Button scarlettQuickBtn = PillButton(S["ScarlettControlsBtn"], ShowScarlett2i2ControlsModal);
        scarlettQuickBtn.SetSingleLine(true);
        scarlettQuickBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        scarlettRow.AddView(scarlettQuickBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(scarlettRow);

        RefreshModalLiveState();

        Android.App.Dialog dlg = OneUiModalDialog.Show(
            this,
            S["VocalModalTitle"],
            customContent: content,
            icon: OneUiIconType.Microphone,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["CloseBtn"],
            onHelp: () => ShowModalGuidedHelp(S["VocalModalHelpTitle"], S["VocalModalHelpBody"], OneUiIconType.Microphone),
            helpContentDesc: S["VocalModalHelpTitle"]);

        Java.Lang.Runnable pollRunnable = null;
        pollRunnable = new Java.Lang.Runnable(() => {
            if (dialogClosed || uiDestroyed) {
                return;
            }
            RefreshModalLiveState();
            uiHandler?.PostDelayed(pollRunnable, 250);
        });
        uiHandler?.PostDelayed(pollRunnable, 250);

        if (dlg != null) {
            dlg.DismissEvent += (_, _) => {
                dialogClosed = true;
                if (pollRunnable != null) {
                    uiHandler?.RemoveCallbacks(pollRunnable);
                }
                if (playerEngine?.LiveMicBridge?.IsCapturingVoiceprint == true) {
                    playerEngine.CancelMicVoiceprintCalibration();
                }
            };
        }
    }

    void ShowCalibrateRttModal() {
        OneUiModalDialog.Show(
            this,
            S["ProAudioCalibrateModalTitle"],
            S["ProAudioCalibrateModalBody"],
            icon: OneUiIconType.Bolt,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["ProAudioCalibrateStartBtn"],
            onPositive: () => {
                RequestRecordAudioPermissionIfNeeded();
                if (!store.ProAudioEnabled) {
                    store.ProAudioEnabled = true;
                }
                audioOutput?.ApplyProAudioConfig(store.BuildProAudioConfig());
                audioOutput?.Start();
                ShowToast(S["ProAudioCalibrateRunningToast"]);
                playerEngine?.StartRoundTripLatencyCalibration(result => RunOnUiThread(() => {
                    if (uiDestroyed) {
                        return;
                    }
                    if (result.IsValid) {
                        store.CalibratedRoundTripSamples = (float)result.LatencySamples;
                        store.CalibratedRoundTripMs = (float)result.LatencyMs;
                        ApplyAndRefreshProAudio();
                        ShowToast(string.Format(
                            S["ProAudioCalibrateSuccessToast"],
                            result.LatencyMs,
                            result.LatencySamples,
                            result.ConfidenceSnr));
                    } else {
                        ShowToast(string.Format(S["ProAudioCalibrateFailedToast"], result.ConfidenceSnr));
                    }
                }));
            },
            negativeText: S["CancelBtn"]);
    }

    void RunScarlettUsbAsync(Action usbOp, Action onDoneUi = null) {
        if (usbOp == null) {
            return;
        }
        _ = Task.Run(() => {
            try {
                usbOp();
            } catch (Exception ex) {
                AppLogger.Warn($"Scarlett USB async op: {ex.Message}");
            }
            if (onDoneUi != null && !uiDestroyed) {
                RunOnUiThread(() => {
                    if (!uiDestroyed) {
                        onDoneUi();
                    }
                });
            }
        });
    }

    void ShowScarlett2i2ControlsModal() {
        if (usbProAudioManager == null) {
            return;
        }
        // Offload USB scan + Scarlett handshake + initial register read off UI main thread (prevents ANR/jank)
        _ = Task.Run(() => {
            UsbProAudioDeviceSnapshot snap = usbProAudioManager.ScanAttachedDevices(autoOpenIfPermitted: true);
            ScarlettHardwareState st = usbProAudioManager.ScarlettState;
            if (snap != null && snap.IsAttached && snap.HasPermission && st != null &&
                (st.Generation == ScarlettGeneration.Gen3 || st.Generation == ScarlettGeneration.Gen4)) {
                usbProAudioManager.TryInitializeScarlettControl(exitMsdMode: false);
                st = usbProAudioManager.ScarlettState;
                if (st.Generation == ScarlettGeneration.Gen3 || st.IsGen4FcpControlTableVerified) {
                    usbProAudioManager.ReadScarlettHardwareState();
                    st = usbProAudioManager.ScarlettState;
                }
            }
            RunOnUiThread(() => {
                if (!uiDestroyed) {
                    ShowScarlett2i2ControlsModalLoaded(snap, st);
                }
            });
        });
    }

    void ShowScarlett2i2ControlsModalLoaded(UsbProAudioDeviceSnapshot snap, ScarlettHardwareState st) {
        Action showScarlettHelp = () => ShowModalGuidedHelp(S["ScarlettHelpTitle"], S["ScarlettHelpBody"], OneUiIconType.Sliders);

        if (snap == null || !snap.IsAttached || !snap.HasPermission || st == null || st.Generation == ScarlettGeneration.None) {
            OneUiModalDialog.Show(
                this,
                S["ScarlettModalTitle"],
                S["ScarlettNotConnectedMsg"],
                icon: OneUiIconType.Sliders,
                iconBadgeColor: OneUiTheme.IconSpatial,
                positiveText: S["ProAudioGrantUsbBtn"],
                onPositive: () => {
                    usbProAudioManager?.RequestUsbPermission();
                    RefreshProAudioUi();
                },
                negativeText: S["CloseBtn"],
                onHelp: showScarlettHelp,
                helpContentDesc: S["ScarlettHelpTitle"]);
            return;
        }

        if (st.Generation == ScarlettGeneration.Gen2) {
            OneUiModalDialog.Show(
                this,
                st.ModelName,
                S["ScarlettGen2Notice"],
                icon: OneUiIconType.Sliders,
                iconBadgeColor: OneUiTheme.IconSpatial,
                positiveText: S["CloseBtn"],
                onHelp: showScarlettHelp,
                helpContentDesc: S["ScarlettHelpTitle"]);
            return;
        }

        if (st.Generation == ScarlettGeneration.Gen4 && !st.IsGen4FcpControlTableVerified) {
            LinearLayout gen4Content = new(this) { Orientation = Orientation.Vertical };
            string hdrText = st.FirmwareVersion > 0
                ? $"{st.ModelName} (PID 0x{st.ProductId:X4} · FW {st.FirmwareVersion})"
                : $"{st.ModelName} (PID 0x{st.ProductId:X4})";
            TextView gen4Hdr = Label(hdrText, 13f, OneUiTheme.Primary);
            gen4Hdr.Typeface = OneUiTheme.GetAppleFontBold(this);
            gen4Content.AddView(gen4Hdr, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });

            TextView fcpNotice = Label(S["ScarlettGen4FcpNotice"], 11.6f, OneUiTheme.TextSub);
            gen4Content.AddView(fcpNotice, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(10)
            });

            float livePeak = playerEngine?.LiveMicBridge?.PeakDbfs ?? -120f;
            TextView assistReadout = Label(
                string.Format(S["ScarlettMetersFormat"], livePeak, livePeak),
                11.4f,
                OneUiTheme.TextSub);
            gen4Content.AddView(assistReadout, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });

            LinearLayout gen4BtnRow = HRow();
            Button gainAssistBtn = PillButton(S["ScarlettGainAssistBtn"], () => {
                float curPeak = playerEngine?.LiveMicBridge?.PeakDbfs ?? -120f;
                if (curPeak <= -115f) {
                    ShowToast(S["ScarlettGainAssistNoSignalToast"]);
                    return;
                }
                float rec1 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(0, curPeak);
                float rec2 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(1, curPeak);
                assistReadout.Text = string.Format(S["ScarlettGainAssistFormat"], rec1, rec2);
            }, active: true);
            gen4BtnRow.AddView(gainAssistBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            gen4Content.AddView(gen4BtnRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            OneUiModalDialog.Show(
                this,
                S["ScarlettModalTitle"],
                customContent: gen4Content,
                icon: OneUiIconType.Sliders,
                iconBadgeColor: OneUiTheme.IconSpatial,
                positiveText: S["CloseBtn"],
                onHelp: showScarlettHelp,
                helpContentDesc: S["ScarlettHelpTitle"]);
            return;
        }

        bool hasCh2Preamp = st.ProductId != Scarlett2Protocol.PidScarlettSoloGen4;
        bool hasDirectMonitor = st.ProductId != Scarlett2Protocol.PidScarlett4i4Gen4;

        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        string verifiedHeader = st.FirmwareVersion > 0
            ? $"{st.ModelName} (PID 0x{st.ProductId:X4} · FW {st.FirmwareVersion})"
            : $"{st.ModelName} (PID 0x{st.ProductId:X4})";
        TextView modelHdr = Label(verifiedHeader, 13f, OneUiTheme.Primary);
        modelHdr.Typeface = OneUiTheme.GetAppleFontBold(this);
        content.AddView(modelHdr, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(2)
        });

        if (st.Generation == ScarlettGeneration.Gen4 && st.IsGen4FcpControlTableVerified) {
            TextView fcpBadge = Label(
                string.Format(S["ScarlettGen4VerifiedBadgeFmt"], st.FirmwareVersion),
                11.0f,
                OneUiTheme.FunctionalGreen);
            content.AddView(fcpBadge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(4)
            });
        }

        TextView safetyNote = Label(S["Scarlett48vSafetyNote"], 11.2f, OneUiTheme.PeachText);
        content.AddView(safetyNote, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        bool linked = st.InputsLinked;
        if (st.SupportsStereoLink) {
            AddSwitchRow(content, S["ScarlettLinkTitle"], linked, v => {
                linked = v;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettGen4InputLink(v));
            });
            AddDivider(content);
        }

        bool ch1Phantom = st.PhantomPowerCh1;
        bool ch2Phantom = st.PhantomPowerCh2;
        AddSwitchRow(content, S["Scarlett48vCh1Title"], ch1Phantom, v => {
            ch1Phantom = v;
            if (!st.SupportsIndependentPreampGain || !hasCh2Preamp || linked) {
                ch2Phantom = v;
            }
            bool p1 = ch1Phantom;
            bool p2 = ch2Phantom;
            RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettPhantomPower(p1, p2));
        });

        if (st.SupportsIndependentPreampGain && hasCh2Preamp) {
            AddDivider(content);
            AddSwitchRow(content, S["Scarlett48vCh2Title"], ch2Phantom, v => {
                ch2Phantom = v;
                if (linked) {
                    ch1Phantom = v;
                }
                bool p1 = ch1Phantom;
                bool p2 = ch2Phantom;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettPhantomPower(p1, p2));
            });
        }

        AddDivider(content);
        ScarlettAirMode air1 = st.AirCh1;
        ScarlettAirMode air2 = st.AirCh2;
        string[] airLabels = st.Generation == ScarlettGeneration.Gen4
            ? [S["ScarlettAirOff"], S["ScarlettAirPresence"], S["ScarlettAirDrive"]]
            : [S["ScarlettAirOff"], S["ScarlettAirPresence"]];

        OneUiInlineSelector air1Sel = new(this, S["ScarlettAirCh1Title"]);
        air1Sel.SetItems(airLabels, Math.Clamp((int)air1, 0, airLabels.Length - 1));
        air1Sel.SelectionChanged += pos => {
            air1 = (ScarlettAirMode)pos;
            if (!hasCh2Preamp || linked) {
                air2 = air1;
            }
            ScarlettAirMode a1 = air1;
            ScarlettAirMode a2 = air2;
            RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettAirMode(a1, a2));
        };
        content.AddView(air1Sel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(6)
        });

        if (hasCh2Preamp) {
            OneUiInlineSelector air2Sel = new(this, S["ScarlettAirCh2Title"]);
            air2Sel.SetItems(airLabels, Math.Clamp((int)air2, 0, airLabels.Length - 1));
            air2Sel.SelectionChanged += pos => {
                air2 = (ScarlettAirMode)pos;
                if (linked) {
                    air1 = air2;
                }
                ScarlettAirMode a1 = air1;
                ScarlettAirMode a2 = air2;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettAirMode(a1, a2));
            };
            content.AddView(air2Sel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        bool inst1 = st.InstrumentModeCh1;
        bool inst2 = st.InstrumentModeCh2;
        AddSwitchRow(content, S["ScarlettInstCh1Title"], inst1, v => {
            inst1 = v;
            if (!hasCh2Preamp || linked) {
                inst2 = v;
            }
            bool i1 = inst1;
            bool i2 = inst2;
            RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettInstrumentMode(i1, i2));
        });
        if (hasCh2Preamp) {
            AddDivider(content);
            AddSwitchRow(content, S["ScarlettInstCh2Title"], inst2, v => {
                inst2 = v;
                if (linked) {
                    inst1 = v;
                }
                bool i1 = inst1;
                bool i2 = inst2;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettInstrumentMode(i1, i2));
            });
        }

        if (hasDirectMonitor) {
            AddDivider(content);
            OneUiInlineSelector dmSel = new(this, S["ScarlettDirectMonTitle"]);
            string[] dmLabels = [S["ScarlettDirectMonOff"], S["ScarlettDirectMonMono"], S["ScarlettDirectMonStereo"]];
            dmSel.SetItems(dmLabels, Math.Clamp((int)st.DirectMonitor, 0, dmLabels.Length - 1));
            dmSel.SelectionChanged += pos => {
                ScarlettDirectMonitorMode dm = (ScarlettDirectMonitorMode)pos;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettDirectMonitor(dm));
            };
            content.AddView(dmSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(4),
                BottomMargin = Dp(6)
            });

            if (st.Generation == ScarlettGeneration.Gen4) {
                AddSliderRow(content, S["ScarlettDirectMonMixTitle"], 0, 100, st.DirectMonitorMixPct, v => {
                    RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettDirectMonitorMix(v));
                }, "%", defaultValue: 50);
            }
        }

        OneUiSeekBar gainBar1 = null;
        OneUiSeekBar gainBar2 = null;
        TextView autoGainStatusLabel = null;
        if (st.SupportsIndependentPreampGain && hasCh2Preamp) {
            float g1 = st.InputGainCh1Db;
            float g2 = st.InputGainCh2Db;
            bool safe1 = st.ClipSafeCh1;
            bool safe2 = st.ClipSafeCh2;

            AddDivider(content);
            (gainBar1, _) = AddSliderRow(content, S["ScarlettGainCh1Title"], 0, 69, (int)MathF.Round(g1), v => {
                g1 = v;
                if (linked) {
                    g2 = v;
                }
                float c1 = g1;
                float c2 = g2;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettGen4PreampGain(c1, c2));
            }, " dB", defaultValue: 24);
            (gainBar2, _) = AddSliderRow(content, S["ScarlettGainCh2Title"], 0, 69, (int)MathF.Round(g2), v => {
                g2 = v;
                if (linked) {
                    g1 = v;
                }
                float c1 = g1;
                float c2 = g2;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettGen4PreampGain(c1, c2));
            }, " dB", defaultValue: 24);

            // Gen 4 Hardware Auto Gain controls (Ch 1, Ch 2, Cancel + live status)
            autoGainStatusLabel = Label(
                string.Format(S["ScarlettAutoGainStatusFormat"], st.AutoGainStatusCh1, st.AutoGainStatusCh2),
                11.2f,
                OneUiTheme.TextSub);
            content.AddView(autoGainStatusLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(4),
                BottomMargin = Dp(4)
            });

            LinearLayout autoRow = HRow();
            Button auto1Btn = PillButton(S["ScarlettAutoGainCh1Btn"], () => {
                int targetCh = linked ? -1 : 0;
                bool ok = false;
                RunScarlettUsbAsync(() => ok = usbProAudioManager.StartScarlettGen4AutoGain(targetCh), () => {
                    if (ok) {
                        ScarlettHardwareState latest = usbProAudioManager.ScarlettState;
                        autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"],
                            latest.AutoGainStatusCh1,
                            latest.AutoGainStatusCh2);
                        ShowToast(S["ScarlettAutoGainRunningToast"]);
                    }
                });
            }, active: true);
            Button auto2Btn = PillButton(S["ScarlettAutoGainCh2Btn"], () => {
                int targetCh = linked ? -1 : 1;
                bool ok = false;
                RunScarlettUsbAsync(() => ok = usbProAudioManager.StartScarlettGen4AutoGain(targetCh), () => {
                    if (ok) {
                        ScarlettHardwareState latest = usbProAudioManager.ScarlettState;
                        autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"],
                            latest.AutoGainStatusCh1,
                            latest.AutoGainStatusCh2);
                        ShowToast(S["ScarlettAutoGainRunningToast"]);
                    }
                });
            });
            Button autoStopBtn = PillButton(S["ScarlettAutoGainCancelBtn"], () => {
                ScarlettAutoGainStatus s1 = default;
                ScarlettAutoGainStatus s2 = default;
                RunScarlettUsbAsync(() => {
                    usbProAudioManager.CancelScarlettGen4AutoGain();
                    usbProAudioManager.PollScarlettGen4AutoGainStatus(out s1, out s2);
                }, () => {
                    autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"], s1, s2);
                });
            });
            autoRow.AddView(auto1Btn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
            autoRow.AddView(auto2Btn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
            autoRow.AddView(autoStopBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.85f));
            content.AddView(autoRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });

            AddSwitchRow(content, S["ScarlettClipSafeCh1Title"], safe1, v => {
                safe1 = v;
                if (linked) {
                    safe2 = v;
                }
                bool cs1 = safe1;
                bool cs2 = safe2;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettGen4ClipSafe(cs1, cs2));
            });
            AddDivider(content);
            AddSwitchRow(content, S["ScarlettClipSafeCh2Title"], safe2, v => {
                safe2 = v;
                if (linked) {
                    safe1 = v;
                }
                bool cs1 = safe1;
                bool cs2 = safe2;
                RunScarlettUsbAsync(() => usbProAudioManager.SetScarlettGen4ClipSafe(cs1, cs2));
            });
        }

        TextView meterLabel = Label(
            string.Format(S["ScarlettMetersFormat"], st.InputMeterCh1Dbfs, st.InputMeterCh2Dbfs),
            11.4f,
            OneUiTheme.TextSub);
        content.AddView(meterLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6),
            BottomMargin = Dp(4)
        });

        TextView gainAssistLabel = Label(string.Empty, 11.2f, OneUiTheme.Primary);
        gainAssistLabel.Visibility = ViewStates.Gone;
        content.AddView(gainAssistLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        LinearLayout actionRow = HRow();
        Button metersBtn = PillButton(S["ScarlettReadMetersBtn"], () => {
            bool ok = false;
            float m1 = -120f;
            float m2 = -120f;
            RunScarlettUsbAsync(() => ok = usbProAudioManager.PollScarlettHardwareMeters(out m1, out m2), () => {
                if (ok) {
                    meterLabel.Text = string.Format(S["ScarlettMetersFormat"], m1, m2);
                }
            });
        }, active: true);
        Button assistBtn = PillButton(S["ScarlettGainAssistBtn"], () => {
            float fallbackPeak = playerEngine?.LiveMicBridge?.PeakDbfs ?? -120f;
            RunScarlettUsbAsync(() => usbProAudioManager.PollScarlettHardwareMeters(out _, out _), () => {
                ScarlettHardwareState latest = usbProAudioManager.ScarlettState;
                if (latest.InputMeterCh1Dbfs <= -115f && latest.InputMeterCh2Dbfs <= -115f && fallbackPeak <= -115f) {
                    ShowToast(S["ScarlettGainAssistNoSignalToast"]);
                    return;
                }
                float rec1 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(0, fallbackPeak);
                float rec2 = usbProAudioManager.ComputeRecommendedSoftwareAutoGainDb(1, fallbackPeak);
                gainAssistLabel.Text = string.Format(S["ScarlettGainAssistFormat"], rec1, rec2);
                gainAssistLabel.Visibility = ViewStates.Visible;
            });
        });
        actionRow.AddView(metersBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        actionRow.AddView(assistBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(actionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        LinearLayout nvramRow = HRow();
        Button saveBtn = PillButton(S["ScarlettSaveNvramBtn"], () => {
            bool ok = false;
            RunScarlettUsbAsync(() => ok = usbProAudioManager.SaveScarlettConfigToNvram(), () => {
                if (ok) {
                    ShowToast(S["ScarlettSaveNvramDoneToast"]);
                }
            });
        });
        Button msdBtn = PillButton(S["ScarlettExitMsdBtn"], () => {
            bool ok = false;
            RunScarlettUsbAsync(() => ok = usbProAudioManager.TryInitializeScarlettControl(exitMsdMode: true), () => {
                if (ok) {
                    ShowToast(S["ScarlettExitMsdDoneToast"]);
                }
            });
        });
        nvramRow.AddView(saveBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        nvramRow.AddView(msdBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(nvramRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        bool modalClosed = false;
        int pollInFlight = 0;
        Android.App.Dialog dlg = OneUiModalDialog.Show(
            this,
            S["ScarlettModalTitle"],
            customContent: content,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.IconSpatial,
            positiveText: S["CloseBtn"],
            onHelp: showScarlettHelp,
            helpContentDesc: S["ScarlettHelpTitle"]);

        Java.Lang.Runnable scarlettPoll = null;
        scarlettPoll = new Java.Lang.Runnable(() => {
            if (modalClosed || uiDestroyed || usbProAudioManager == null) {
                return;
            }
            ScarlettHardwareState cur = usbProAudioManager.ScarlettState;
            if (autoGainStatusLabel != null &&
                (cur.AutoGainStatusCh1 == ScarlettAutoGainStatus.Running ||
                 cur.AutoGainStatusCh2 == ScarlettAutoGainStatus.Running) &&
                Interlocked.CompareExchange(ref pollInFlight, 1, 0) == 0) {
                bool ok = false;
                ScarlettAutoGainStatus s1 = default;
                ScarlettAutoGainStatus s2 = default;
                RunScarlettUsbAsync(() => {
                    try {
                        ok = usbProAudioManager.PollScarlettGen4AutoGainStatus(out s1, out s2);
                    } finally {
                        Interlocked.Exchange(ref pollInFlight, 0);
                    }
                }, () => {
                    if (!modalClosed && ok) {
                        autoGainStatusLabel.Text = string.Format(S["ScarlettAutoGainStatusFormat"], s1, s2);
                        ScarlettHardwareState updated = usbProAudioManager.ScarlettState;
                        SyncSlider(gainBar1, (int)MathF.Round(updated.InputGainCh1Db), 0, 69, " dB");
                        SyncSlider(gainBar2, (int)MathF.Round(updated.InputGainCh2Db), 0, 69, " dB");
                    }
                });
            }
            uiHandler?.PostDelayed(scarlettPoll, 750);
        });
        uiHandler?.PostDelayed(scarlettPoll, 750);
        if (dlg != null) {
            dlg.DismissEvent += (_, _) => {
                modalClosed = true;
                if (scarlettPoll != null) {
                    uiHandler?.RemoveCallbacks(scarlettPoll);
                }
            };
        }
    }

    void ShowProAudioCreditsModal() {
        OneUiModalDialog.Show(
            this,
            S["ProAudioCreditsTitle"],
            Cavernize.Logic.Spatial.ProAudioDuplexDiagnostics.BuildAttributionNotice(),
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"]);
    }
}
