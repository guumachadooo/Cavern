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
    void BuildSettingsDiagnosticsCard() {
        // Diagnostics & Shared Logs
        LinearLayout logCard = Card(S["DiagnosticsTitle"], OneUiIconType.Logs, OneUiTheme.IconSystemInfo);
        logPathInfoText = Label("", 11.5f, ColMuted);
        logPathInfoText.SetPadding(0, 0, 0, Dp(8));
        logCard.AddView(logPathInfoText);

        LinearLayout logBtns = HRow();
        Button viewLogBtn = PillButton(S["ViewLogBtn"], () => {
            ShowMessageDialog(S["DiagnosticsTitle"], AppLogger.ReadRecentLines(450), OneUiIconType.Logs, monospace: true);
        }, active: true);
        Button exportLogBtn = PillButton(S["ExportLogBtn"], () => {
            string logPath = AppLogger.SharedLogPath;
            if (string.IsNullOrEmpty(logPath)) {
                Toast.MakeText(this, S["LogFileNotAvailableToast"], ToastLength.Short).Show();
                return;
            }
            _ = Task.Run(() => {
                bool exists = File.Exists(logPath);
                if (!uiDestroyed) {
                    RunOnUiThread(() => {
                        if (exists) {
                            ShareLocalFile(logPath);
                        } else {
                            Toast.MakeText(this, S["LogFileNotAvailableToast"], ToastLength.Short).Show();
                        }
                    });
                }
            });
        });
        Button clearLogBtn = PillButton(S["ClearLogBtn"], () => {
            AppLogger.Clear(this);
            RefreshLogSummaryLabel();
            Toast.MakeText(this, S["LogClearedMsg"], ToastLength.Short).Show();
        });
        logBtns.AddView(viewLogBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        logBtns.AddView(exportLogBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        logBtns.AddView(clearLogBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        logCard.AddView(logBtns);

        AddDivider(logCard);
        LinearLayout selfCheckHeader = HRow();
        TextView selfCheckTitle = Label(S["SelfCheckSectionTitle"], 13f, ColText);
        selfCheckTitle.SetTypeface(null, TypefaceStyle.Bold);
        selfCheckHeader.AddView(selfCheckTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        selfCheckHeader.AddView(CreateInlineHelpBtn(S["SelfCheckHelpTitle"], S["SelfCheckHelpBody"]));
        logCard.AddView(selfCheckHeader);

        selfCheckStatusText = Label("", 11.5f, OneUiTheme.TextSub);
        selfCheckStatusText.SetPadding(0, Dp(2), 0, Dp(8));
        logCard.AddView(selfCheckStatusText);

        LinearLayout selfCheckBtns = HRow();
        selfCheckQuickBtn = PillButton(S["SelfCheckQuickBtn"], () => _ = RunSelfCheckAsync(SelfCheckMode.Quick));
        selfCheckFullBtn = PillButton(S["SelfCheckFullBtn"], () => _ = RunSelfCheckAsync(SelfCheckMode.Full));
        selfCheckUltraBtn = PillButton(S["SelfCheckUltraBtn"], () => _ = RunSelfCheckAsync(SelfCheckMode.Ultra), active: true);
        selfCheckBtns.AddView(selfCheckQuickBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(5) });
        selfCheckBtns.AddView(selfCheckFullBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(5) });
        selfCheckBtns.AddView(selfCheckUltraBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.1f));
        logCard.AddView(selfCheckBtns);

        AddSwitchRow(logCard, S["SelfCheckAutoAfterUpdateSwitch"], store.AutoSelfCheckAfterUpdate, v => store.AutoSelfCheckAfterUpdate = v);

        AddDivider(logCard);
        LinearLayout omniHeader = HRow();
        TextView omniTitle = Label(S["OmniBridgeSectionTitle"], 13f, ColText);
        omniTitle.SetTypeface(null, TypefaceStyle.Bold);
        omniHeader.AddView(omniTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        omniHeader.AddView(CreateInlineHelpBtn(S["OmniBridgeHelpTitle"], S["OmniBridgeHelpBody"]));
        logCard.AddView(omniHeader);

        omniBridgeStatusText = Label("", 11.5f, OneUiTheme.TextSub);
        omniBridgeStatusText.SetPadding(0, Dp(2), 0, Dp(10));
        logCard.AddView(omniBridgeStatusText);

        LinearLayout omniBtns = HRow();
        omniBridgeOpenBtn = PillButton(S["OmniBridgeOpenBtn"], OpenOmniAgentOrDownload, active: true);
        Button omniAdvancedBtn = PillButton(S["OmniBridgeAdvancedBtn"], ShowCloudLogSyncModal);
        omniBtns.AddView(omniBridgeOpenBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f) { RightMargin = Dp(6) });
        omniBtns.AddView(omniAdvancedBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        logCard.AddView(omniBtns);

        RefreshLogSummaryLabel();
        diagnosticsDeveloperCardRef = logCard;
        RegisterSettingsCard(logCard, categoryIndex: 5, isProOnly: true, "diagnostics logs error debug export clear cloud github upload sync crash checkup selfcheck ultra audit ui test pairwise bridge live cloudflare relay hub telemetry biometric fingerprint wake standby omniagent ipc");

    }

    void RefreshLogSummaryLabel() {
        if (logPathInfoText != null) {
            long bytes = AppLogger.GetLogSizeBytes();
            logPathInfoText.Text = string.Format(
                S["LogSummaryHealthFmt"],
                AppLogger.SharedLogPath,
                bytes / 1024.0,
                AppLogger.SessionFatalCount,
                AppLogger.SessionErrorCount,
                AppLogger.SessionWarnCount);
        }
        if (cloudLogStatusText != null && store != null) {
            bool useCf = CloudLogSyncService.ShouldUseCloudflare(store);
            bool ready = CloudLogSyncService.IsUploadReady(store);
            if (!ready) {
                cloudLogStatusText.Text = S["CloudLogStatusNotConfigured"];
                cloudLogStatusText.SetTextColor(OneUiTheme.TextSub);
            } else {
                string lastSync = S["CloudLogNeverSynced"];
                if (!string.IsNullOrWhiteSpace(store.CloudLogLastSyncUtc) &&
                    DateTime.TryParse(store.CloudLogLastSyncUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsedUtc)) {
                    string localTime = parsedUtc.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
                    string st = string.IsNullOrWhiteSpace(store.CloudLogLastSyncStatus) ? "OK" : store.CloudLogLastSyncStatus;
                    lastSync = $"{localTime} ({st})";
                } else if (!string.IsNullOrWhiteSpace(store.CloudLogLastSyncStatus)) {
                    lastSync = store.CloudLogLastSyncStatus;
                }
                if (useCf && LiveDiagnosticBridgeService.TryNormalizeRelayUrl(store.LiveRelayUrl, out string cfUrl)) {
                    string cfHost = new Uri(cfUrl).Host;
                    cloudLogStatusText.Text = string.Format(S["CloudLogCloudflareReadyFmt"], cfHost, lastSync);
                } else {
                    (string branch, _, _) = CloudLogSyncService.ResolveTarget(store);
                    cloudLogStatusText.Text = string.Format(S["CloudLogStatusReadyFmt"], store.CloudLogRepo, branch, lastSync);
                }
                cloudLogStatusText.SetTextColor(
                    (store.CloudLogLastSyncStatus ?? "").StartsWith("ERR:", StringComparison.OrdinalIgnoreCase)
                        ? OneUiTheme.FunctionalAmber
                        : OneUiTheme.FunctionalGreen);
            }
        }
        if (cloudLogUploadBtn != null) {
            cloudLogUploadBtn.Enabled = !isCloudLogUploading;
        }
        if (selfCheckStatusText != null && store != null && !isSelfCheckRunning) {
            if (string.IsNullOrWhiteSpace(store.LastSelfCheckSummary)) {
                selfCheckStatusText.Text = S["SelfCheckNeverRun"];
                selfCheckStatusText.SetTextColor(OneUiTheme.TextSub);
            } else {
                string when = "";
                if (!string.IsNullOrWhiteSpace(store.LastSelfCheckUtc) &&
                    DateTime.TryParse(store.LastSelfCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime scUtc)) {
                    when = scUtc.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
                }
                selfCheckStatusText.Text = string.Format(S["SelfCheckLastRunFmt"], string.IsNullOrEmpty(when) ? "-" : when, store.LastSelfCheckSummary);
                if (store.LastSelfCheckSummary.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)) {
                    selfCheckStatusText.SetTextColor(ColDanger);
                } else if (store.LastSelfCheckSummary.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) {
                    selfCheckStatusText.SetTextColor(OneUiTheme.FunctionalAmber);
                } else {
                    selfCheckStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
                }
            }
        }
        if (selfCheckQuickBtn != null) {
            selfCheckQuickBtn.Enabled = !isSelfCheckRunning;
        }
        if (selfCheckFullBtn != null) {
            selfCheckFullBtn.Enabled = !isSelfCheckRunning;
        }
        if (selfCheckUltraBtn != null) {
            selfCheckUltraBtn.Enabled = !isSelfCheckRunning;
        }
        if (liveBridgeStatusText != null && store != null) {
            bool active = LiveDiagnosticBridgeService.IsActive;
            bool hasUrl = LiveDiagnosticBridgeService.TryNormalizeRelayUrl(store.LiveRelayUrl, out string normUrl);
            if (active) {
                int rem = LiveDiagnosticBridgeService.RemainingSeconds;
                string mmss = $"{rem / 60:00}:{rem % 60:00}";
                liveBridgeStatusText.Text = string.Format(
                    S["LiveBridgeActiveFmt"],
                    mmss,
                    LiveDiagnosticBridgeService.ActiveMaxTier,
                    LiveDiagnosticBridgeService.CommandsHandled,
                    LiveDiagnosticBridgeService.ActiveSessionCode,
                    LiveDiagnosticBridgeService.LastEventText);
                liveBridgeStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
            } else if (hasUrl && store.LiveBridgeStandbyEnabled && LiveDiagnosticBridgeService.HasPairedSecret(store)) {
                string host = new Uri(normUrl).Host;
                liveBridgeStatusText.Text = string.Format(S["LiveBridgeStandbyActiveFmt"], host, store.LiveBridgeDefaultTier);
                liveBridgeStatusText.SetTextColor(OneUiTheme.PrimaryHigh);
            } else if (hasUrl) {
                string host = new Uri(normUrl).Host;
                liveBridgeStatusText.Text = string.Format(S["LiveBridgeIdleFmt"], host, store.LiveBridgeDefaultTier);
                liveBridgeStatusText.SetTextColor(OneUiTheme.TextSub);
            } else {
                liveBridgeStatusText.Text = S["LiveBridgeNotConfigured"];
                liveBridgeStatusText.SetTextColor(OneUiTheme.TextSub);
            }
            if (liveBridgeStandbySwitch != null && liveBridgeStandbySwitch.Checked != store.LiveBridgeStandbyEnabled) {
                liveBridgeStandbySwitch.Checked = store.LiveBridgeStandbyEnabled;
            }
            if (liveBridgeToggleBtn != null) {
                liveBridgeToggleBtn.Text = active ? S["LiveBridgeStopBtn"] : S["LiveBridgeStartBtn"];
                liveBridgeToggleBtn.Enabled = !isLiveBridgeBusy;
            }
            if (liveBridgeCopyBtn != null) {
                liveBridgeCopyBtn.Visibility = active ? ViewStates.Visible : ViewStates.Gone;
            }
        }
        if (omniBridgeStatusText != null && store != null) {
            bool installed = OmniBridgeIpcReceiver.IsOmniAgentInstalledWithMatchingSignature(this);
            if (installed) {
                omniBridgeStatusText.Text = string.Format(S["OmniBridgeStatusConnectedFmt"], LiveDiagnosticBridgeService.CommandsHandled);
                omniBridgeStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
            } else {
                omniBridgeStatusText.Text = S["OmniBridgeStatusNotInstalled"];
                omniBridgeStatusText.SetTextColor(OneUiTheme.TextSub);
            }
            if (omniBridgeOpenBtn != null) {
                omniBridgeOpenBtn.Text = installed ? S["OmniBridgeOpenBtn"] : S["OmniBridgeInstallBtn"];
            }
        }
    }

    void OpenOmniAgentOrDownload() {
        try {
            Intent launch = PackageManager?.GetLaunchIntentForPackage(OmniBridgeIpcReceiver.OmniAgentPackage);
            if (launch != null) {
                launch.AddFlags(ActivityFlags.NewTask);
                StartActivity(launch);
                return;
            }
            Intent browser = new(Intent.ActionView, Android.Net.Uri.Parse("https://github.com/guumachadooo/OmniAgent/releases/latest"));
            StartActivity(browser);
        } catch (Exception ex) {
            Toast.MakeText(this, ex.Message, ToastLength.Short).Show();
        }
    }

    void CopyActiveLiveBridgeCode() {
        string code = LiveDiagnosticBridgeService.ActiveSessionCode;
        if (string.IsNullOrEmpty(code)) {
            return;
        }
        try {
            if (GetSystemService(ClipboardService) is ClipboardManager cm) {
                cm.PrimaryClip = ClipData.NewPlainText("Cavernize Live Code", code);
                Toast.MakeText(this, S["LiveBridgeCodeCopiedToast"], ToastLength.Short).Show();
            }
        } catch {
            // Best effort
        }
    }

    async Task ToggleLiveBridgeSessionAsync() {
        if (isLiveBridgeBusy || store == null) {
            return;
        }
        if (LiveDiagnosticBridgeService.IsActive) {
            isLiveBridgeBusy = true;
            try {
                await LiveDiagnosticBridgeService.StopSessionAsync();
            } finally {
                isLiveBridgeBusy = false;
                if (!uiDestroyed) {
                    RunOnUiThread(RefreshLogSummaryLabel);
                }
            }
            return;
        }

        if (!LiveDiagnosticBridgeService.TryNormalizeRelayUrl(store.LiveRelayUrl, out _)) {
            Toast.MakeText(this, S["LiveBridgeNeedUrlToast"], ToastLength.Short).Show();
            ShowLiveBridgeConfigModal();
            return;
        }

        isLiveBridgeBusy = true;
        if (liveBridgeToggleBtn != null) {
            liveBridgeToggleBtn.Enabled = false;
        }
        try {
            (bool ok, string code, string msg) = await LiveDiagnosticBridgeService.StartSessionAsync(this, store);
            if (uiDestroyed) {
                return;
            }
            RunOnUiThread(() => {
                RefreshLogSummaryLabel();
                if (ok && !string.IsNullOrEmpty(code)) {
                    CopyActiveLiveBridgeCode();
                } else {
                    Toast.MakeText(this, msg, ToastLength.Long).Show();
                }
            });
        } finally {
            isLiveBridgeBusy = false;
            if (!uiDestroyed) {
                RunOnUiThread(RefreshLogSummaryLabel);
            }
        }
    }

    void ShowLiveBridgeConfigModal() {
        if (store == null) {
            return;
        }
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        body.SetPadding(0, Dp(2), 0, Dp(4));

        TextView urlLbl = Label(S["LiveBridgeUrlLabel"], 12f, OneUiTheme.TextSub);
        urlLbl.SetPadding(0, Dp(4), 0, Dp(4));
        body.AddView(urlLbl);

        EditText urlInput = new(this) {
            Text = store.LiveRelayUrl ?? "",
            Hint = "https://cavernize-diag-relay.your-name.workers.dev",
            TextSize = 13f,
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri
        };
        urlInput.SetSingleLine(true);
        urlInput.SetTextColor(ColText);
        urlInput.SetHintTextColor(ColMuted);
        urlInput.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        urlInput.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
        body.AddView(urlInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        OneUiInlineSelector tierSel = new(this, S["LiveBridgeTierLabel"]);
        tierSel.SetItems([S["LiveBridgeTier0Short"], S["LiveBridgeTier1Short"], S["LiveBridgeTier2Short"]], store.LiveBridgeDefaultTier);
        int selectedTier = store.LiveBridgeDefaultTier;
        tierSel.SelectionChanged += pos => selectedTier = pos;
        body.AddView(tierSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        int[] durations = [15, 30, 60];
        OneUiInlineSelector durSel = new(this, S["LiveBridgeDurationLabel"]);
        int curDurIdx = Math.Max(0, Array.IndexOf(durations, store.LiveBridgeSessionMinutes));
        durSel.SetItems(["15 min", "30 min", "60 min"], curDurIdx);
        int selectedMinutes = durations[curDurIdx];
        durSel.SelectionChanged += pos => selectedMinutes = durations[Math.Clamp(pos, 0, durations.Length - 1)];
        body.AddView(durSel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        TextView pairStatus = Label("", 11.5f, OneUiTheme.TextSub);
        pairStatus.SetPadding(0, Dp(2), 0, Dp(6));
        body.AddView(pairStatus);

        void RefreshPairStatus() {
            bool paired = LiveDiagnosticBridgeService.HasPairedSecret(store);
            pairStatus.Text = paired ? S["LiveBridgePairStatusPaired"] : S["LiveBridgePairStatusUnpaired"];
            pairStatus.SetTextColor(paired ? OneUiTheme.FunctionalGreen : OneUiTheme.TextSub);
        }
        RefreshPairStatus();

        LinearLayout pairRow = HRow();
        Button genPairBtn = PillButton(S["LiveBridgePairGenBtn"], () => {
            string copyable = LiveDiagnosticBridgeService.GenerateAndStorePairSecret(store);
            RefreshPairStatus();
            if (!string.IsNullOrEmpty(copyable) && GetSystemService(ClipboardService) is ClipboardManager cm) {
                cm.PrimaryClip = ClipData.NewPlainText("Cavernize Bridge Pair Key", copyable);
                Toast.MakeText(this, S["LiveBridgePairCopiedToast"], ToastLength.Long).Show();
            }
            RefreshLogSummaryLabel();
        }, active: true);
        Button copyPairBtn = PillButton(S["LiveBridgePairCopyBtn"], () => {
            string copyable = LiveDiagnosticBridgeService.GetCopyablePairSecret(store);
            if (string.IsNullOrEmpty(copyable)) {
                copyable = LiveDiagnosticBridgeService.GenerateAndStorePairSecret(store);
                RefreshPairStatus();
            }
            if (!string.IsNullOrEmpty(copyable) && GetSystemService(ClipboardService) is ClipboardManager cm) {
                cm.PrimaryClip = ClipData.NewPlainText("Cavernize Bridge Pair Key", copyable);
                Toast.MakeText(this, S["LiveBridgePairCopiedToast"], ToastLength.Long).Show();
            }
        });
        pairRow.AddView(genPairBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f) { RightMargin = Dp(6) });
        pairRow.AddView(copyPairBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        body.AddView(pairRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        AddSwitchRow(body, S["LiveBridgeSmartKnockSwitch"], store.LiveBridgeSmartKnockEnabled, v => {
            store.LiveBridgeSmartKnockEnabled = v;
        });

        Button copyKnockIdBtn = PillButton(S["LiveBridgeCopyKnockIdBtn"], () => {
            string kid = LiveDiagnosticBridgeService.GetPublicKnockId(store);
            if (string.IsNullOrEmpty(kid)) {
                LiveDiagnosticBridgeService.GenerateAndStorePairSecret(store);
                RefreshPairStatus();
                kid = LiveDiagnosticBridgeService.GetPublicKnockId(store);
            }
            if (!string.IsNullOrEmpty(kid) && GetSystemService(ClipboardService) is ClipboardManager cm) {
                cm.PrimaryClip = ClipData.NewPlainText("Cavernize Public Knock ID", kid);
                Toast.MakeText(this, S["LiveBridgeKnockIdCopiedToast"], ToastLength.Long).Show();
            }
        });
        body.AddView(copyKnockIdBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2), BottomMargin = Dp(6) });

        TextView testStatus = Label("", 11.5f, OneUiTheme.TextSub);
        testStatus.SetPadding(0, Dp(2), 0, Dp(6));
        body.AddView(testStatus);

        LinearLayout linksRow = HRow();
        Button openCfBtn = PillButton(S["LiveBridgeOpenCloudflareBtn"], () => {
            try {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://dash.cloudflare.com")));
            } catch {
                // Ignore if no browser available
            }
        });
        Button openWorkerCodeBtn = PillButton(S["LiveBridgeOpenWorkerCodeBtn"], () => {
            try {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://raw.githubusercontent.com/guumachadooo/Cavern/master/.agents/docs/cloudflare_live_relay_worker.js")));
            } catch {
                // Ignore if no browser available
            }
        });
        linksRow.AddView(openCfBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        linksRow.AddView(openWorkerCodeBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        body.AddView(linksRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        LinearLayout utilRow = HRow();
        Button testBtn = PillButton(S["LiveBridgeTestBtn"], () => {
            string candidate = urlInput.Text?.Trim() ?? "";
            testStatus.Text = S["LiveBridgeTesting"];
            testStatus.SetTextColor(OneUiTheme.PrimaryHigh);
            _ = Task.Run(async () => {
                (bool ok, string msg) = await LiveDiagnosticBridgeService.TestRelayHealthAsync(candidate);
                if (!uiDestroyed) {
                    RunOnUiThread(() => {
                        testStatus.Text = msg;
                        testStatus.SetTextColor(ok ? OneUiTheme.FunctionalGreen : ColDanger);
                    });
                }
            });
        });
        Button revokeBtn = PillButton(S["LiveBridgePairRevokeBtn"], () => {
            LiveDiagnosticBridgeService.RevokePairSecret(store);
            LiveBridgeWakeService.SyncStandbyState(this, store);
            RefreshPairStatus();
            RefreshLogSummaryLabel();
            Toast.MakeText(this, S["LiveBridgePairRevokedToast"], ToastLength.Short).Show();
        });
        utilRow.AddView(testBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(6) });
        utilRow.AddView(revokeBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.9f));
        body.AddView(utilRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        OneUiModalDialog.Show(
            this,
            S["LiveBridgeModalTitle"],
            customContent: body,
            icon: OneUiIconType.Logs,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloudLogSaveBtn"],
            onPositive: () => {
                store.LiveBridgeDefaultTier = selectedTier;
                store.LiveBridgeSessionMinutes = selectedMinutes;
                string raw = urlInput.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(raw)) {
                    store.LiveRelayUrl = "";
                    store.LiveBridgeStandbyEnabled = false;
                    LiveBridgeWakeService.SyncStandbyState(this, store);
                    RefreshLogSummaryLabel();
                    Toast.MakeText(this, S["LiveBridgeSavedToast"], ToastLength.Short).Show();
                    return;
                }
                if (!LiveDiagnosticBridgeService.TryNormalizeRelayUrl(raw, out string clean)) {
                    Toast.MakeText(this, S["LiveBridgeInvalidUrlToast"], ToastLength.Long).Show();
                    return;
                }
                store.LiveRelayUrl = clean;
                if (LiveDiagnosticBridgeService.HasPairedSecret(store) && !CloudLogSyncService.IsGitHubConfigured(store)) {
                    store.CloudLogProviderMode = 0;
                    store.CloudLogSyncEnabled = true;
                }
                LiveBridgeWakeService.SyncStandbyState(this, store);
                RefreshLogSummaryLabel();
                Toast.MakeText(this, S["LiveBridgeSavedToast"], ToastLength.Short).Show();
            },
            negativeText: S["CancelBtn"],
            onHelp: () => ShowModalGuidedHelp(S["LiveBridgeHelpTitle"], S["LiveBridgeHelpBody"], OneUiIconType.Logs));
    }

    Task RunSelfCheckAsync(bool fullSuite) =>
        RunSelfCheckAsync(fullSuite ? SelfCheckMode.Full : SelfCheckMode.Quick);

    async Task RunSelfCheckAsync(SelfCheckMode mode) {
        if (isSelfCheckRunning || store == null) {
            return;
        }
        isSelfCheckRunning = true;
        if (selfCheckQuickBtn != null) selfCheckQuickBtn.Enabled = false;
        if (selfCheckFullBtn != null) selfCheckFullBtn.Enabled = false;
        if (selfCheckUltraBtn != null) selfCheckUltraBtn.Enabled = false;
        string modeLabel = mode switch {
            SelfCheckMode.Ultra => S["SelfCheckUltraBtn"],
            SelfCheckMode.Full => S["SelfCheckFullBtn"],
            _ => S["SelfCheckQuickBtn"]
        };
        if (selfCheckStatusText != null) {
            selfCheckStatusText.Text = string.Format(S["SelfCheckRunningFmt"], modeLabel);
            selfCheckStatusText.SetTextColor(OneUiTheme.PrimaryHigh);
        }

        try {
            SelfCheckReport report = await SelfDiagnosticEngine.RunAsync(
                this,
                store,
                mode,
                stepName => {
                    if (!uiDestroyed && selfCheckStatusText != null) {
                        RunOnUiThread(() => {
                            selfCheckStatusText.Text = string.Format(S["SelfCheckRunningFmt"], stepName);
                        });
                    }
                });

            (_, long installedBuild) = AppUpdateService.GetInstalledVersion(this);
            if (installedBuild > 0) {
                store.LastSelfCheckBuildCode = installedBuild;
            }
            store.LastSelfCheckUtc = report.ExecutedAtUtc;
            store.LastSelfCheckSummary = report.CompactSummary;

            bool autoSynced = false;
            if (CloudLogSyncService.IsConfigured(store)) {
                string reason = mode switch {
                    SelfCheckMode.Ultra => "manual_ultra",
                    SelfCheckMode.Full => "manual_full",
                    _ => "manual_quick"
                };
                CloudLogSyncResult syncRes = await CloudLogSyncService.UploadSelfCheckReportAsync(this, store, report, reason);
                autoSynced = syncRes.Success;
            }

            if (uiDestroyed) {
                return;
            }
            RunOnUiThread(() => {
                RefreshLogSummaryLabel();
                Toast.MakeText(
                    this,
                    autoSynced ? S["SelfCheckAutoSyncedToast"] : string.Format(S["SelfCheckCompletedToastFmt"], report.CompactSummary),
                    ToastLength.Long).Show();
                ShowSelfCheckReportModal(report, autoSynced);
            });
        } catch (Exception ex) {
            AppLogger.Error("SYS", "RunSelfCheckAsync failed", ex);
            if (!uiDestroyed) {
                RunOnUiThread(() => Toast.MakeText(this, $"Self-Check error: {ex.Message}", ToastLength.Long).Show());
            }
        } finally {
            isSelfCheckRunning = false;
            if (!uiDestroyed) {
                RunOnUiThread(RefreshLogSummaryLabel);
            }
        }
    }

    void ShowSelfCheckReportModal(SelfCheckReport report, bool alreadySynced) {
        if (report == null) {
            return;
        }
        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        body.SetPadding(0, Dp(2), 0, Dp(4));

        TextView summaryLbl = Label(
            string.Format(
                S["SelfCheckModalSummaryFmt"],
                report.Mode.ToUpperInvariant(),
                report.PassCount,
                report.WarnCount,
                report.FailCount,
                report.SkipCount,
                report.TotalElapsedMs),
            12.5f,
            report.FailCount > 0 ? ColDanger : (report.WarnCount > 0 ? OneUiTheme.FunctionalAmber : OneUiTheme.FunctionalGreen));
        summaryLbl.SetTypeface(null, TypefaceStyle.Bold);
        summaryLbl.SetPadding(0, 0, 0, Dp(8));
        body.AddView(summaryLbl);

        foreach (SelfCheckItem item in report.Checks) {
            LinearLayout row = new(this) { Orientation = Orientation.Vertical };
            row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
            row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 12f, OneUiTheme.BorderSubtle, 1f);

            string badge = item.Status switch {
                SelfCheckStatus.Pass => "PASS",
                SelfCheckStatus.Warn => "WARN",
                SelfCheckStatus.Fail => "FAIL",
                _ => "SKIP"
            };
            Android.Graphics.Color badgeCol = item.Status switch {
                SelfCheckStatus.Pass => OneUiTheme.FunctionalGreen,
                SelfCheckStatus.Warn => OneUiTheme.FunctionalAmber,
                SelfCheckStatus.Fail => ColDanger,
                _ => ColMuted
            };

            TextView titleLine = Label($"[{badge}] {item.Layer} · {item.Name} ({item.ElapsedMs}ms)", 12f, badgeCol);
            titleLine.SetTypeface(null, TypefaceStyle.Bold);
            row.AddView(titleLine);

            if (!string.IsNullOrWhiteSpace(item.Details)) {
                TextView detLine = Label(item.Details, 11f, ColMuted);
                detLine.SetPadding(0, Dp(2), 0, 0);
                row.AddView(detLine);
            }

            body.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }

        LinearLayout actionsRow = HRow();
        Button copyBtn = PillButton(S["SelfCheckCopyReportBtn"], () => {
            try {
                if (GetSystemService(ClipboardService) is ClipboardManager cm) {
                    cm.PrimaryClip = ClipData.NewPlainText("Cavernize Self-Check", report.ToMarkdown());
                    Toast.MakeText(this, S["SelfCheckCopiedToast"], ToastLength.Short).Show();
                }
            } catch {
                // Best effort
            }
        });
        actionsRow.AddView(copyBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        Button uploadBtn = PillButton(S["SelfCheckUploadReportBtn"], () => {
            if (!CloudLogSyncService.IsUploadReady(store)) {
                if (CloudLogSyncService.ShouldUseCloudflare(store)) {
                    Toast.MakeText(this, S["CloudLogCloudflareNeedBridgeToast"], ToastLength.Long).Show();
                    ShowLiveBridgeConfigModal();
                } else {
                    Toast.MakeText(this, S["CloudLogNeedTokenToast"], ToastLength.Short).Show();
                    ShowCloudLogSyncModal();
                }
                return;
            }
            _ = Task.Run(async () => {
                CloudLogSyncResult r = await CloudLogSyncService.UploadSelfCheckReportAsync(this, store, report, "manual_modal");
                if (!uiDestroyed) {
                    RunOnUiThread(() => {
                        RefreshLogSummaryLabel();
                        Toast.MakeText(this, r.Message, ToastLength.Long).Show();
                    });
                }
            });
        }, active: !alreadySynced);
        actionsRow.AddView(uploadBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        body.AddView(actionsRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

        OneUiModalDialog.Show(
            this,
            string.Format(S["SelfCheckModalTitleFmt"], report.OverallVerdict),
            customContent: body,
            icon: OneUiIconType.Logs,
            iconBadgeColor: report.FailCount > 0 ? ColDanger : OneUiTheme.IconSystemInfo,
            positiveText: S["CloseBtn"],
            onHelp: () => ShowModalGuidedHelp(S["SelfCheckHelpTitle"], S["SelfCheckHelpBody"], OneUiIconType.Logs));
    }

    async Task UploadDiagnosticLogsToCloudAsync() {
        if (isCloudLogUploading || store == null) {
            return;
        }
        if (!CloudLogSyncService.IsUploadReady(store)) {
            if (CloudLogSyncService.ShouldUseCloudflare(store)) {
                Toast.MakeText(this, S["CloudLogCloudflareNeedBridgeToast"], ToastLength.Long).Show();
                ShowLiveBridgeConfigModal();
            } else {
                Toast.MakeText(this, S["CloudLogNeedTokenToast"], ToastLength.Short).Show();
                ShowCloudLogSyncModal();
            }
            return;
        }

        isCloudLogUploading = true;
        if (cloudLogUploadBtn != null) {
            cloudLogUploadBtn.Enabled = false;
        }
        if (cloudLogStatusText != null) {
            cloudLogStatusText.Text = S["CloudLogUploading"];
            cloudLogStatusText.SetTextColor(OneUiTheme.PrimaryHigh);
        }

        try {
            CloudLogSyncResult res = await CloudLogSyncService.UploadLogsAsync(this, store, "manual");
            if (uiDestroyed) {
                return;
            }
            RunOnUiThread(() => {
                RefreshLogSummaryLabel();
                Toast.MakeText(this, res.Message, ToastLength.Long).Show();
            });
        } finally {
            isCloudLogUploading = false;
            if (!uiDestroyed) {
                RunOnUiThread(RefreshLogSummaryLabel);
            }
        }
    }

    void ShowCloudLogSyncModal() {
        if (store == null) {
            return;
        }

        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        body.SetPadding(0, Dp(2), 0, Dp(4));

        bool draftEnabled = store.CloudLogSyncEnabled;
        int draftProvider = Math.Clamp(store.CloudLogProviderMode, 0, 1);
        int draftBranchMode = store.CloudLogBranchMode;
        bool draftAutoCrash = store.CloudLogAutoOnCrash;
        bool draftAutoError = store.CloudLogAutoOnError;

        Switch enableSw = AddSwitchRow(body, S["CloudLogEnableSwitch"], draftEnabled, v => draftEnabled = v);

        OneUiInlineSelector providerSelector = new(this, S["CloudLogProviderLabel"]);
        providerSelector.SetItems([S["CloudLogProviderCloudflare"], S["CloudLogProviderGitHub"]], draftProvider);
        body.AddView(providerSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6), BottomMargin = Dp(8) });

        // Cloudflare Unified Hub v3 section
        LinearLayout cfSection = new(this) { Orientation = Orientation.Vertical };
        bool cfReady = CloudLogSyncService.IsCloudflareConfigured(store);
        string cfInfoMsg = cfReady && LiveDiagnosticBridgeService.TryNormalizeRelayUrl(store.LiveRelayUrl, out string cfRelayUrl)
            ? string.Format(S["CloudLogCloudflareReadyFmt"], new Uri(cfRelayUrl).Host, "v3")
            : S["CloudLogCloudflareNeedBridgeToast"];
        TextView cfInfoLbl = Label(cfInfoMsg, 12f, cfReady ? OneUiTheme.FunctionalGreen : OneUiTheme.FunctionalAmber);
        cfInfoLbl.SetPadding(0, Dp(2), 0, Dp(8));
        cfSection.AddView(cfInfoLbl);

        Button openBridgeBtn = PillButton(S["CloudLogOpenBridgeBtn"], ShowLiveBridgeConfigModal);
        cfSection.AddView(openBridgeBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });
        body.AddView(cfSection);

        // Optional Legacy GitHub Repository section
        LinearLayout githubSection = new(this) { Orientation = Orientation.Vertical };

        TextView repoLbl = Label(S["CloudLogRepoLabel"], 12f, OneUiTheme.TextSub);
        repoLbl.SetPadding(0, Dp(4), 0, Dp(4));
        githubSection.AddView(repoLbl);

        EditText repoInput = new(this) {
            Text = string.IsNullOrWhiteSpace(store.CloudLogRepo) ? CloudLogSyncService.DefaultRepo : store.CloudLogRepo,
            Hint = CloudLogSyncService.DefaultRepo,
            TextSize = 13f,
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri
        };
        repoInput.SetSingleLine(true);
        repoInput.SetTextColor(ColText);
        repoInput.SetHintTextColor(ColMuted);
        repoInput.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        repoInput.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
        githubSection.AddView(repoInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        OneUiInlineSelector branchSelector = new(this, S["CloudLogBranchLabel"]);
        branchSelector.SetItems([S["CloudLogBranchDiagnostics"], S["CloudLogBranchMaster"]], Math.Clamp(draftBranchMode, 0, 1));
        branchSelector.SelectionChanged += pos => draftBranchMode = pos;
        githubSection.AddView(branchSelector, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        TextView tokenLbl = Label(S["CloudLogTokenLabel"], 12f, OneUiTheme.TextSub);
        tokenLbl.SetPadding(0, Dp(2), 0, Dp(4));
        githubSection.AddView(tokenLbl);

        bool hasSaved = CloudLogSyncService.HasStoredToken(store);
        EditText tokenInput = new(this) {
            Text = "",
            Hint = hasSaved ? S["CloudLogTokenHintSaved"] : S["CloudLogTokenHintNew"],
            TextSize = 13f,
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword
        };
        tokenInput.SetSingleLine(true);
        tokenInput.SetTextColor(ColText);
        tokenInput.SetHintTextColor(ColMuted);
        tokenInput.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        tokenInput.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
        githubSection.AddView(tokenInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        if (hasSaved) {
            Button clearTokBtn = PillButton(S["CloudLogClearTokenBtn"], () => {
                CloudLogSyncService.ClearToken(store);
                tokenInput.Text = "";
                tokenInput.Hint = S["CloudLogTokenHintNew"];
                if (draftProvider == 1) {
                    enableSw.Checked = false;
                    draftEnabled = false;
                }
                RefreshLogSummaryLabel();
                Toast.MakeText(this, S["CloudLogTokenClearedToast"], ToastLength.Short).Show();
            });
            githubSection.AddView(clearTokBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }
        body.AddView(githubSection);

        AddSwitchRow(body, S["CloudLogAutoCrashSwitch"], draftAutoCrash, v => draftAutoCrash = v);
        AddSwitchRow(body, S["CloudLogAutoErrorSwitch"], draftAutoError, v => draftAutoError = v);

        TextView modalStatus = Label("", 11.5f, OneUiTheme.TextSub);
        modalStatus.SetPadding(0, Dp(6), 0, Dp(6));
        modalStatus.Visibility = ViewStates.Gone;
        body.AddView(modalStatus);

        LinearLayout actionRow = HRow();
        actionRow.SetPadding(0, Dp(4), 0, 0);
        Button testBtn = PillButton(S["CloudLogTestBtn"], () => {
            modalStatus.Visibility = ViewStates.Visible;
            modalStatus.Text = S["CloudLogTesting"];
            modalStatus.SetTextColor(OneUiTheme.PrimaryHigh);
            int providerForTest = draftProvider;
            string testRepo = repoInput.Text?.Trim() ?? "";
            string testTok = tokenInput.Text?.Trim() ?? "";
            _ = Task.Run(async () => {
                CloudLogSyncResult r = providerForTest == 0
                    ? await CloudLogSyncService.TestCloudflareConnectionAsync(store)
                    : await CloudLogSyncService.TestConnectionAsync(store, testRepo, testTok);
                if (!uiDestroyed) {
                    RunOnUiThread(() => {
                        modalStatus.Visibility = ViewStates.Visible;
                        modalStatus.Text = r.Message;
                        modalStatus.SetTextColor(r.Success ? OneUiTheme.FunctionalGreen : ColDanger);
                    });
                }
            });
        });
        Button createTokBtn = PillButton(S["CloudLogCreateTokenBtn"], () => {
            try {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(CloudLogSyncService.GitHubTokenCreateUrl)));
            } catch {
                // Ignore if no browser available
            }
        });
        actionRow.AddView(testBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(6) });
        actionRow.AddView(createTokBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        body.AddView(actionRow);

        void RefreshProviderVisibility() {
            bool useCf = draftProvider == 0;
            cfSection.Visibility = useCf ? ViewStates.Visible : ViewStates.Gone;
            githubSection.Visibility = useCf ? ViewStates.Gone : ViewStates.Visible;
            createTokBtn.Visibility = useCf ? ViewStates.Gone : ViewStates.Visible;
        }
        providerSelector.SelectionChanged += pos => {
            draftProvider = Math.Clamp(pos, 0, 1);
            RefreshProviderVisibility();
        };
        RefreshProviderVisibility();

        OneUiModalDialog.Show(
            this,
            S["CloudLogModalTitle"],
            customContent: body,
            icon: OneUiIconType.Logs,
            iconBadgeColor: OneUiTheme.IconSystemInfo,
            positiveText: S["CloudLogSaveBtn"],
            onPositive: () => {
                store.CloudLogProviderMode = draftProvider;
                store.CloudLogAutoOnCrash = draftAutoCrash;
                store.CloudLogAutoOnError = draftAutoError;
                if (draftProvider == 0) {
                    store.CloudLogSyncEnabled = draftEnabled && CloudLogSyncService.IsCloudflareConfigured(store);
                    RefreshLogSummaryLabel();
                    Toast.MakeText(this, S["CloudLogSavedToast"], ToastLength.Short).Show();
                    return;
                }
                string repoVal = repoInput.Text?.Trim() ?? "";
                if (!CloudLogSyncService.TryParseRepo(repoVal, out string owner, out string repo)) {
                    Toast.MakeText(this, S["CloudLogInvalidRepoToast"], ToastLength.Short).Show();
                    return;
                }
                string enteredToken = tokenInput.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(enteredToken)) {
                    if (!CloudLogSyncService.SaveToken(store, enteredToken)) {
                        Toast.MakeText(this, S["CloudLogEncryptFailedToast"], ToastLength.Long).Show();
                        return;
                    }
                    draftEnabled = true;
                }
                store.CloudLogRepo = $"{owner}/{repo}";
                store.CloudLogBranchMode = draftBranchMode;
                store.CloudLogSyncEnabled = draftEnabled && CloudLogSyncService.HasStoredToken(store);
                RefreshLogSummaryLabel();
                Toast.MakeText(this, S["CloudLogSavedToast"], ToastLength.Short).Show();
            },
            negativeText: S["CancelBtn"],
            onHelp: () => ShowModalGuidedHelp(S["CloudLogHelpTitle"], S["CloudLogHelpBody"], OneUiIconType.Logs));
    }

}
