using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Biometrics;
using Android.OS;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Language;
using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Services;

/// <summary>
/// Translucent One UI 9 overlay Activity launched when a paired AI sends a verified wake request.
/// Displays the exact requested permission tier, duration, and reason, and immediately invokes
/// native Android <see cref="BiometricPrompt"/> (fingerprint / face / device PIN) to approve.
/// </summary>
[Activity(
    Exported = false,
    ExcludeFromRecents = true,
    LaunchMode = LaunchMode.SingleTop,
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
public sealed class LiveBridgeAuthActivity : Activity {
    public const string ExtraWakeId = "cavernize.livebridge.wake_id";
    const int ReqConfirmCredential = 4401;
    const int AuthenticatorBiometricStrong = 0x000F;
    const int AuthenticatorDeviceCredential = 0x8000;

    LiveBridgeWakeRequest activeRequest;
    CancellationSignal biometricCancel;
    TextView statusLabel;
    Button approveBtn;
    Button denyBtn;
    bool resolved;
    bool fallbackManualApprove;

    protected override void OnCreate(Bundle savedInstanceState) {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(27)) {
            SetShowWhenLocked(true);
            SetTurnScreenOn(true);
        }

        string wakeId = Intent?.GetStringExtra(ExtraWakeId) ?? "";
        activeRequest = LiveDiagnosticBridgeService.GetPendingWakeRequest(wakeId);
        if (activeRequest == null) {
            Finish();
            return;
        }

        BuildOverlayUi();
        Window?.DecorView?.Post(TriggerBiometricPrompt);
    }

    int Dp(float dp) => (int)Math.Round(dp * (Resources?.DisplayMetrics?.Density ?? 2.5f));

    void BuildOverlayUi() {
        AndroidStrings S = AndroidStrings.Active;
        FrameLayout root = new(this);
        root.SetBackgroundColor(Color.Argb(185, 8, 12, 20));
        root.SetPadding(Dp(20), Dp(28), Dp(20), Dp(28));

        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(22), Dp(22), Dp(22), Dp(20));
        card.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceCard, 26f, OneUiTheme.BorderSubtle, 1.2f);

        TextView title = new(this) {
            Text = S["LiveBridgeAuthTitle"],
            TextSize = 18f
        };
        title.SetTypeface(null, TypefaceStyle.Bold);
        title.SetTextColor(OneUiTheme.TextPrimary);
        card.AddView(title);

        string tierLabel = activeRequest.RequestedTier switch {
            0 => S["LiveBridgeTier0Short"],
            1 => S["LiveBridgeTier1Short"],
            _ => S["LiveBridgeTier2Short"]
        };

        TextView details = new(this) {
            Text = string.Format(
                S["LiveBridgeAuthBodyFmt"],
                activeRequest.Requester,
                tierLabel,
                activeRequest.DurationSeconds / 60,
                string.IsNullOrWhiteSpace(activeRequest.Reason) ? "—" : activeRequest.Reason),
            TextSize = 13.5f
        };
        details.SetTextColor(OneUiTheme.TextSub);
        details.SetPadding(0, Dp(10), 0, Dp(12));
        card.AddView(details);

        statusLabel = new TextView(this) {
            Text = S["LiveBridgeAuthTouchFingerprint"],
            TextSize = 12.5f
        };
        statusLabel.SetTextColor(OneUiTheme.PrimaryHigh);
        statusLabel.SetPadding(0, 0, 0, Dp(16));
        card.AddView(statusLabel);

        LinearLayout btnRow = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        btnRow.SetGravity(GravityFlags.CenterVertical);

        denyBtn = CreateActionButton(S["LiveBridgeDenyBtn"], OneUiTheme.SurfaceModalRow, OneUiTheme.TextPrimary);
        denyBtn.Click += (_, _) => _ = DenyAndFinishAsync("Denied by user");

        approveBtn = CreateActionButton(S["LiveBridgeAuthRetryBtn"], OneUiTheme.PrimaryHigh, Color.White);
        approveBtn.Click += (_, _) => {
            if (fallbackManualApprove) {
                _ = ApproveAndFinishAsync(biometricVerified: false);
            } else {
                TriggerBiometricPrompt();
            }
        };

        btnRow.AddView(denyBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        btnRow.AddView(approveBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.2f));
        card.AddView(btnRow);

        FrameLayout.LayoutParams cardLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Center
        };
        root.AddView(card, cardLp);
        SetContentView(root);
    }

    Button CreateActionButton(string label, Color bg, Color fg) {
        Button b = new(this) {
            Text = label,
            TextSize = 13f,
            AllCaps = false
        };
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetTextColor(fg);
        b.SetPadding(Dp(12), Dp(11), Dp(12), Dp(11));
        b.Background = OneUiTheme.CreateRoundedDrawable(this, bg, 18f);
        return b;
    }

    void TriggerBiometricPrompt() {
        if (resolved || activeRequest == null) {
            return;
        }
        AndroidStrings S = AndroidStrings.Active;
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
            try {
                biometricCancel?.Cancel();
                biometricCancel = new CancellationSignal();
                BiometricPrompt.Builder builder = new BiometricPrompt.Builder(this)
                    .SetTitle(S["LiveBridgeAuthTitle"])
                    .SetSubtitle(string.Format(S["LiveBridgeAuthPromptSubFmt"], activeRequest.RequestedTier, activeRequest.DurationSeconds / 60))
                    .SetDescription(activeRequest.Reason);

                if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
                    // BiometricStrong | DeviceCredential — MUST NOT set negative button when DeviceCredential is enabled
                    builder.SetAllowedAuthenticators(AuthenticatorBiometricStrong | AuthenticatorDeviceCredential);
                } else if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
#pragma warning disable CS0618, CA1422
                    builder.SetDeviceCredentialAllowed(true);
#pragma warning restore CS0618, CA1422
                } else {
                    builder.SetNegativeButton(S["LiveBridgeDenyBtn"], MainExecutor, new NegativeClickListener(() => _ = DenyAndFinishAsync("User cancelled biometric prompt")));
                }

                BiometricPrompt prompt = builder.Build();
                prompt.Authenticate(biometricCancel, MainExecutor, new PromptAuthCallback(this));
                return;
            } catch (Exception ex) {
                AppLogger.Warn("CLOUD", $"BiometricPrompt unavailable ({ex.GetType().Name}: {ex.Message}); trying Keyguard fallback.");
            }
        }

        // Fallback for API 26-27 or devices with no enrolled biometrics
        if (GetSystemService(KeyguardService) is KeyguardManager km && km.IsDeviceSecure) {
            try {
#pragma warning disable CS0618, CA1422
                Intent confirm = km.CreateConfirmDeviceCredentialIntent(S["LiveBridgeAuthTitle"], activeRequest.Reason);
                if (confirm != null) {
                    StartActivityForResult(confirm, ReqConfirmCredential);
                    return;
                }
#pragma warning restore CS0618, CA1422
            } catch {
                // Fall through to manual prompt message
            }
        }

        fallbackManualApprove = true;
        statusLabel.Text = S["LiveBridgeAuthNoBiometricHint"];
        approveBtn.Text = S["LiveBridgeApproveManualBtn"];
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data) {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ReqConfirmCredential) {
            if (resultCode == Result.Ok) {
                _ = ApproveAndFinishAsync(biometricVerified: true);
            } else {
                _ = DenyAndFinishAsync("Device credential cancelled");
            }
        }
    }

    async Task ApproveAndFinishAsync(bool biometricVerified) {
        if (resolved || activeRequest == null) {
            return;
        }
        resolved = true;
        RunOnUiThread(() => {
            if (approveBtn != null) approveBtn.Enabled = false;
            if (denyBtn != null) denyBtn.Enabled = false;
            if (statusLabel != null) {
                statusLabel.Text = AndroidStrings.Active["LiveBridgeAuthConnecting"];
                statusLabel.SetTextColor(OneUiTheme.FunctionalGreen);
            }
        });

        AppSettingsStore store = new(this);
        (bool ok, string msg) = await LiveDiagnosticBridgeService.CompleteWakeApprovalAsync(
            this,
            store,
            activeRequest.WakeId,
            activeRequest.RequestedTier,
            activeRequest.DurationSeconds,
            biometricVerified).ConfigureAwait(false);

        RunOnUiThread(() => {
            CancelWakeNotification();
            Toast.MakeText(this, ok ? msg : $"Live Bridge: {msg}", ToastLength.Short).Show();
            Finish();
        });
    }

    async Task DenyAndFinishAsync(string reason) {
        if (resolved || activeRequest == null) {
            return;
        }
        resolved = true;
        CancelWakeNotification();
        AppSettingsStore store = new(this);
        await LiveDiagnosticBridgeService.DenyWakeRequestAsync(store, activeRequest.WakeId, reason).ConfigureAwait(false);
        RunOnUiThread(Finish);
    }

    void CancelWakeNotification() {
        try {
            if (GetSystemService(NotificationService) is NotificationManager nm) {
                nm.Cancel(9056);
            }
        } catch {
            // Ignore
        }
    }

    protected override void OnDestroy() {
        try {
            biometricCancel?.Cancel();
        } catch {
            // Ignore
        }
        base.OnDestroy();
    }

    sealed class NegativeClickListener(Action onClick) : Java.Lang.Object, IDialogInterfaceOnClickListener {
        public void OnClick(IDialogInterface dialog, int which) => onClick?.Invoke();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    sealed class PromptAuthCallback(LiveBridgeAuthActivity activity) : BiometricPrompt.AuthenticationCallback {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) {
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                base.OnAuthenticationSucceeded(result);
            }
            _ = activity.ApproveAndFinishAsync(biometricVerified: true);
        }

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence errString) {
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                base.OnAuthenticationError(errorCode, errString);
            }
            string msg = errString?.ToString() ?? errorCode.ToString();
            activity.RunOnUiThread(() => {
                if (activity.statusLabel != null) {
                    activity.statusLabel.Text = msg;
                    activity.statusLabel.SetTextColor(OneUiTheme.TextSub);
                }
            });
            if (errorCode == BiometricErrorCode.UserCanceled || errorCode == BiometricErrorCode.NegativeButton) {
                _ = activity.DenyAndFinishAsync(msg);
            }
        }

        public override void OnAuthenticationFailed() {
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                base.OnAuthenticationFailed();
            }
            activity.RunOnUiThread(() => {
                if (activity.statusLabel != null) {
                    activity.statusLabel.Text = AndroidStrings.Active["LiveBridgeAuthFailedTryAgain"];
                }
            });
        }
    }
}
