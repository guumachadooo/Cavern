using Android;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.External;
using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

using CavernizeAndroid.Audio;
using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using VoidX.WPF.FFmpeg;

using Path = System.IO.Path;

namespace CavernizeAndroid;


public partial class MainActivity {
    void RefreshAllUiState() {
        NotifyUiDomainChanged(UiStateDomain.All, immediate: true);
    }

    void RefreshSettingsDot() {
        ViewStates vis = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
        if (settingsUpdateDot != null) {
            settingsUpdateDot.Visibility = vis;
        }
        if (settingsUpdateDotCollapsed != null) {
            settingsUpdateDotCollapsed.Visibility = vis;
        }
    }

    // Shared One UI 9 Frosted UI Helpers (zero emojis, non-clipping pills, contextual '?' help bubbles)
    int Dp(int dp) => (int)Math.Round(dp * Resources.DisplayMetrics.Density);

    LinearLayout HRow() {
        LinearLayout row = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
    }

    LinearLayout Card(string title = null, OneUiIconType? icon = null, Color? badgeColor = null) {
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.Background = OneUiTheme.CreateGlassCardDrawable(this, null, OneUiTheme.CardCornerDp);
        card.Elevation = Dp(6);
        card.SetPadding(Dp(16), Dp(15), Dp(16), Dp(15));
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(14)
        };
        card.LayoutParameters = lp;
        if (!string.IsNullOrEmpty(title)) {
            LinearLayout hdr = HRow();
            hdr.SetPadding(0, 0, 0, Dp(10));
            if (icon.HasValue && badgeColor.HasValue) {
                hdr.AddView(OneUiTheme.CreateVectorIconBadge(this, icon.Value, badgeColor.Value));
            }
            TextView tv = SectionTitle(title);
            tv.SetPadding(icon.HasValue ? Dp(10) : 0, 0, Dp(8), 0);
            hdr.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            card.AddView(hdr);
        }
        return card;
    }

    View CreateInlineHelpBtn(string optionTitle, string causeEffectHelp) {
        FrameLayout badge = new(this) {
            ContentDescription = optionTitle
        };
        badge.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            OneUiTheme.SurfaceSubTile,
            99f,
            OneUiTheme.BorderSubtle,
            0.8f);
        OneUiTheme.AttachRecoil(badge, 0.88f);
        OneUiIconView ico = new(this, OneUiIconType.Info, OneUiTheme.TextSub);
        badge.AddView(ico, new FrameLayout.LayoutParams(Dp(14), Dp(14)) { Gravity = GravityFlags.Center });
        badge.Click += (_, _) => OneUiModalDialog.Show(
            this,
            optionTitle,
            causeEffectHelp,
            icon: OneUiIconType.Info,
            iconBadgeColor: ColAccent,
            positiveText: S["OkBtn"]);
        return badge;
    }

    void ConfirmDestructiveAction(string title, string message, Action onConfirmed, string confirmButtonText = null) {
        if (uiDestroyed || onConfirmed == null) {
            return;
        }
        OneUiModalDialog.Show(
            this,
            string.IsNullOrWhiteSpace(title) ? S["ConfirmDeleteTitle"] : title,
            message,
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.FunctionalRed,
            positiveText: string.IsNullOrWhiteSpace(confirmButtonText) ? S["ConfirmDeleteActionBtn"] : confirmButtonText,
            onPositive: onConfirmed,
            negativeText: S["CancelBtn"]);
    }

    LinearLayout WrapControlWithHelp(View control, string helpTitle, string helpBody, int bottomMarginDp = 6) {
        LinearLayout row = HRow();
        row.AddView(control, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (!string.IsNullOrEmpty(helpBody)) {
            row.AddView(CreateInlineHelpBtn(helpTitle, helpBody), new LinearLayout.LayoutParams(Dp(26), Dp(26)) {
                LeftMargin = Dp(8)
            });
        }
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(bottomMarginDp)
        };
        return row;
    }

    TextView SectionTitle(string text) {
        TextView tv = new(this) {
            Text = text,
            TextSize = 15.5f
        };
        tv.SetTextColor(ColText);
        tv.Typeface = OneUiTheme.GetAppleFontBold(this);
        tv.SetPadding(0, 0, 0, Dp(8));
        return tv;
    }

    TextView Label(string text, float sizeSp = 13f, Color? color = null) {
        TextView tv = new(this) {
            Text = text,
            TextSize = sizeSp
        };
        tv.Typeface = OneUiTheme.GetAppleFontRegular(this);
        tv.SetTextColor(color ?? ColText);
        return tv;
    }

    Button PrimaryButton(string text, Action onClick) {
        Button b = new(this) { Text = text, TextSize = 13f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(42));
        b.Background = OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp);
        b.SetTextColor(OneUiTheme.OnPrimary);
        b.Typeface = OneUiTheme.GetAppleFontBold(this);
        b.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button SecondaryButton(string text, Action onClick) {
        Button b = new(this) { Text = text, TextSize = 12.5f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(40));
        b.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.ButtonCornerDp);
        b.SetTextColor(ColText);
        b.Typeface = OneUiTheme.GetAppleFontMedium(this);
        b.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button PillButton(string text, Action onClick, bool active = false) {
        Button b = new(this) { Text = text, TextSize = 12f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(40));
        b.Background = active
            ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        b.SetTextColor(active ? OneUiTheme.OnPrimary : ColText);
        b.Typeface = active ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
        b.SetPadding(Dp(11), Dp(8), Dp(11), Dp(8));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    void AddDivider(LinearLayout parent) {
        parent.AddView(OneUiTheme.CreateDivider(this));
    }

    async Task CheckFFmpegUpdateAsync(bool silent) {
        try {
            RunOnUiThread(() => SetFFmpegStatus(S["CheckingUpdate"], -1));
            FFmpegStatusResult status = await FFmpegInstallerService.CheckStatusAsync(this, store);
            latestFFmpegStatus = status;
            AppLogger.Info($"FFmpeg status: installed={status.IsInstalled}, working={status.IsWorking}, local='{status.LocalPath}', ver='{status.InstalledVersion}'");

            RunOnUiThread(() => {
                if (!string.IsNullOrEmpty(status.LocalPath)) {
                    ffmpeg.Location = status.LocalPath;
                }
                RefreshSettingsDot();
                RefreshSettingsTabButtons();
                RefreshFFmpegCardUi();
                RefreshContainerOptions();
                if (status.IsInstalled && !status.IsWorking) {
                    AppLogger.Warn($"Bundled FFmpeg probe failed at '{status.LocalPath}' (ver={status.InstalledVersion}).");
                } else if (!silent && status.IsInstalled && status.IsWorking) {
                    string ver = string.IsNullOrEmpty(status.InstalledVersion) ? "OK" : status.InstalledVersion;
                    Toast.MakeText(this, string.Format(S["FFmpegUpToDate"], ver), ToastLength.Short).Show();
                } else if (!silent && !status.IsInstalled) {
                    ShowError(S["FFmpegNotInstalled"]);
                }
            });
        } catch (Exception ex) {
            AppLogger.Error("CheckFFmpegUpdateAsync", ex);
            RunOnUiThread(() => {
                RefreshFFmpegCardUi();
                if (!silent) {
                    ShowError(ex.Message);
                }
            });
        }
    }

    async Task CheckTruehddUpdateAsync(bool silent) {
        try {
            RunOnUiThread(() => SetTruehddStatus(S["CheckingUpdate"], -1));
            TruehddStatusResult status = await TruehddInstallerService.CheckStatusAsync(this, store);
            latestTruehddStatus = status;
            AppLogger.Info($"truehdd status: installed={status.IsInstalled}, local='{status.LocalPath}', ver='{status.InstalledVersion}'");

            RunOnUiThread(() => {
                if (!string.IsNullOrEmpty(status.LocalPath)) {
                    Truehdd.CustomExecutablePath = status.LocalPath;
                }
                RefreshSettingsDot();
                RefreshSettingsTabButtons();
                RefreshTruehddCardUi();
                if (!silent && status.IsInstalled) {
                    string ver = string.IsNullOrEmpty(status.InstalledVersion) ? "OK" : status.InstalledVersion;
                    Toast.MakeText(this, string.Format(S["TruehddUpToDate"], ver), ToastLength.Short).Show();
                } else if (!silent && !status.IsInstalled) {
                    ShowError(S["TruehddNotInstalled"]);
                }
            });
        } catch (Exception ex) {
            AppLogger.Error("CheckTruehddUpdateAsync", ex);
            RunOnUiThread(() => {
                RefreshTruehddCardUi();
                if (!silent) {
                    ShowError(ex.Message);
                }
            });
        }
    }

    async Task CheckForUpdatesAsync(bool silent) {
        try {
            RunOnUiThread(() => SetSettingsUpdateStatus(S["CheckingUpdate"], -1));
            UpdateCheckResult result = await AppUpdateService.CheckForUpdateAsync(this);
            latestUpdateInfo = result;

            RunOnUiThread(() => {
                RefreshSettingsDot();
                RefreshSettingsUpdateUi();
                if (!result.IsUpdateAvailable && !silent) {
                    Toast.MakeText(this, S["UpToDateMsg"], ToastLength.Short).Show();
                }
            });
        } catch (Exception ex) {
            AppLogger.Error("CheckForUpdatesAsync", ex);
            RunOnUiThread(() => {
                SetSettingsUpdateStatus(ex.Message, 0);
                if (!silent) {
                    ShowError(ex.Message);
                }
            });
        }
    }

    void StartUpdateDownloadAndInstall() {
        if (isUpdateDownloading) {
            return;
        }
        if (!string.IsNullOrEmpty(downloadedUpdateApkPath) && File.Exists(downloadedUpdateApkPath)) {
            InstallDownloadedApk(downloadedUpdateApkPath);
            return;
        }
        if (latestUpdateInfo == null || string.IsNullOrEmpty(latestUpdateInfo.DownloadUrl)) {
            _ = CheckForUpdatesAsync(silent: false);
            return;
        }

        isUpdateDownloading = true;
        RunOnUiThread(() => SetSettingsUpdateStatus(S["DownloadingUpdate"], 0));

        string url = latestUpdateInfo.DownloadUrl;
        long expected = latestUpdateInfo.AssetSizeBytes;
        _ = Task.Run(async () => {
            try {
                string apkPath = await AppUpdateService.DownloadUpdateAsync(this, url, expected, (frac, msg) => {
                    RunOnUiThread(() => SetSettingsUpdateStatus(msg, frac));
                });
                downloadedUpdateApkPath = apkPath;
                RunOnUiThread(() => {
                    isUpdateDownloading = false;
                    RefreshSettingsUpdateUi();
                    InstallDownloadedApk(apkPath);
                });
            } catch (Exception ex) {
                AppLogger.Error("StartUpdateDownloadAndInstall", ex);
                RunOnUiThread(() => {
                    isUpdateDownloading = false;
                    SetSettingsUpdateStatus(ex.Message, 0);
                    ShowError(ex.Message);
                });
            }
        });
    }

    void InstallDownloadedApk(string apkPath) {
        if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath)) {
            return;
        }
        if (!AppUpdateService.CanInstallPackages(this)) {
            pendingInstallApkPath = apkPath;
            OneUiModalDialog.Show(
                this,
                S["UpdateCardTitle"],
                S["AllowUnknownSourcesMsg"],
                icon: OneUiIconType.Info,
                positiveText: "OK",
                onPositive: () => AppUpdateService.OpenUnknownSourcesSettings(this),
                negativeText: S["DismissBtn"]);
            return;
        }
        AppUpdateService.LaunchInstaller(this, apkPath);
    }

    void ShowError(string msg) {
        if (isRunningUiSelfAudit) {
            AppLogger.Info("UI", $"[SelfAudit] Expected modal guard shown: {msg}");
        } else {
            AppLogger.Error($"UI Error shown: {msg}");
        }
        if (uiDestroyed) {
            return;
        }
        OneUiModalDialog.Show(
            this,
            S["ErrorTitle"],
            msg,
            icon: OneUiIconType.Info,
            iconBadgeColor: ColDanger);
    }

    void ShowMessageDialog(string title, string msg, OneUiIconType icon = OneUiIconType.Info, bool monospace = false) {
        if (uiDestroyed) {
            return;
        }
        OneUiModalDialog.Show(
            this,
            title,
            msg,
            icon: icon,
            monospaceBody: monospace);
    }

}
