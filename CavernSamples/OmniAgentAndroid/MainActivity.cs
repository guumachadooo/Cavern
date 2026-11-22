using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;

using OmniAgentAndroid.Services;

namespace OmniAgentAndroid;

/// <summary>
/// One UI 9 inspired AMOLED dashboard for OmniAgent Companion (Phase 2).
/// Zero stock AlertDialog/Spinner, strict BaselineAligned=false anti-clipping, EN + PT-BR auto-locale.
/// </summary>
[Activity(
    Label = "OmniAgent",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity {
    static readonly Color BgDark = Color.ParseColor("#09090B");
    static readonly Color CardBg = Color.ParseColor("#16161B");
    static readonly Color CardElevated = Color.ParseColor("#202028");
    static readonly Color AccentBlue = Color.ParseColor("#2F7CF6");
    static readonly Color AccentGreen = Color.ParseColor("#22C55E");
    static readonly Color AccentRed = Color.ParseColor("#EF4444");
    static readonly Color TextPrimary = Color.ParseColor("#F4F4F6");
    static readonly Color TextSecondary = Color.ParseColor("#9CA3AF");
    static readonly Color StrokeColor = Color.ParseColor("#2A2A35");

    OmniSettingsStore store;
    bool isPt;

    TextView statusBadge;
    TextView statusSubtitle;
    Button toggleServiceBtn;
    Button modeSmartBtn;
    Button modeBioBtn;
    Button modeTrustedBtn;
    TextView relaySummaryTv;
    TextView githubSummaryTv;
    TextView logViewTv;
    FrameLayout modalOverlay;

    protected override void OnCreate(Bundle savedInstanceState) {
        base.OnCreate(savedInstanceState);
        store = new OmniSettingsStore(this);
        isPt = string.Equals(Java.Util.Locale.Default?.Language, "pt", StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
            if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted) {
                RequestPermissions([Android.Manifest.Permission.PostNotifications], 101);
            }
        }

        BuildUi();
        OmniAgentForegroundService.StateChanged += OnServiceOrLogChanged;
        OmniSettingsStore.ActivityChanged += OnServiceOrLogChanged;
        RefreshUi();
    }

    protected override void OnDestroy() {
        OmniAgentForegroundService.StateChanged -= OnServiceOrLogChanged;
        OmniSettingsStore.ActivityChanged -= OnServiceOrLogChanged;
        base.OnDestroy();
    }

    void OnServiceOrLogChanged() => RunOnUiThread(RefreshUi);

    string T(string en, string pt) => isPt ? pt : en;

    int Dp(int v) => (int)Math.Round(TypedValue.ApplyDimension(ComplexUnitType.Dip, v, Resources.DisplayMetrics));

    GradientDrawable Pill(Color fill, int radiusDp, Color? stroke = null) {
        GradientDrawable g = new();
        g.SetColor(fill);
        g.SetCornerRadius(Dp(radiusDp));
        if (stroke.HasValue) {
            g.SetStroke(Dp(1), stroke.Value);
        }
        return g;
    }

    void BuildUi() {
        FrameLayout rootFrame = new(this) {
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };
        rootFrame.SetBackgroundColor(BgDark);

        ScrollView scroll = new(this) {
            LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
            FillViewport = true
        };

        LinearLayout content = new(this) {
            Orientation = Orientation.Vertical,
            LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        };
        int pad = Dp(18);
        content.SetPadding(pad, Dp(46), pad, Dp(32));

        // Header
        TextView title = new(this) { Text = "OmniAgent", TextSize = 28 };
        title.SetTextColor(TextPrimary);
        title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        content.AddView(title);

        TextView subtitle = new(this) {
            Text = T("Antigravity 2 Full-Agent Companion (Zero-Trust E2E)", "Companheiro Agente Full Antigravity 2 (E2E Zero-Trust)"),
            TextSize = 13
        };
        subtitle.SetTextColor(TextSecondary);
        subtitle.SetPadding(0, Dp(2), 0, Dp(16));
        content.AddView(subtitle);

        // 1. Live Status Card
        LinearLayout statusCard = MakeCard();
        LinearLayout statusRow = MakeRow();
        statusBadge = new TextView(this) { TextSize = 13 };
        statusBadge.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        statusBadge.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
        statusBadge.SetSingleLine(true);
        statusRow.AddView(statusBadge);
        statusCard.AddView(statusRow);

        statusSubtitle = new TextView(this) { TextSize = 13 };
        statusSubtitle.SetTextColor(TextSecondary);
        statusSubtitle.SetPadding(0, Dp(8), 0, Dp(14));
        statusCard.AddView(statusSubtitle);

        LinearLayout actionRow = MakeRow();
        toggleServiceBtn = MakeButton(T("Start Agent", "Ligar Agente"), AccentBlue, TextPrimary, true);
        toggleServiceBtn.Click += (_, _) => ToggleService();
        actionRow.AddView(toggleServiceBtn, MakeWeightedLp(1f, rightMarginDp: 8));

        Button termuxBtn = MakeButton(T("Wake Termux", "Abrir Termux"), CardElevated, TextPrimary, false);
        termuxBtn.Click += (_, _) => {
            (bool ok, string msg) = OmniAgentForegroundService.LaunchTermuxCommand(this, store.TermuxStartScript, ["--bg"]);
            OmniSettingsStore.AppendActivity(ok ? "TERMUX" : "ERR", msg);
            Toast.MakeText(this, msg, ToastLength.Short)?.Show();
        };
        actionRow.AddView(termuxBtn, MakeWeightedLp(1f, leftMarginDp: 0));
        statusCard.AddView(actionRow);
        content.AddView(statusCard);

        // 2. Security Mode Card (never nags with fingerprint in Smart mode!)
        LinearLayout secCard = MakeCard();
        secCard.AddView(MakeCardTitle(T("Approval Policy", "Política de Aprovação")));
        TextView secDesc = new(this) {
            Text = T(
                "Smart mode uses 256-bit E2E HMAC silently with zero fingerprint popups. Switch to Biometric only if you want fingerprint prompts on push/install.",
                "Modo Smart usa HMAC E2E de 256 bits sem popups chatos de digital. Troque para Digital apenas se quiser confirmar push/instalação."),
            TextSize = 12
        };
        secDesc.SetTextColor(TextSecondary);
        secDesc.SetPadding(0, Dp(4), 0, Dp(12));
        secCard.AddView(secDesc);

        LinearLayout modeRow = MakeRow();
        modeSmartBtn = MakeModeButton("Smart", "smart");
        modeBioBtn = MakeModeButton(T("Biometric", "Digital"), "biometric");
        modeTrustedBtn = MakeModeButton(T("Trusted", "Livre"), "trusted");
        modeRow.AddView(modeSmartBtn, MakeWeightedLp(1f, rightMarginDp: 6));
        modeRow.AddView(modeBioBtn, MakeWeightedLp(1f, rightMarginDp: 6));
        modeRow.AddView(modeTrustedBtn, MakeWeightedLp(1f));
        secCard.AddView(modeRow);
        content.AddView(secCard);

        // 3. Connections Card (Cloudflare Relay + Direct GitHub Push)
        LinearLayout connCard = MakeCard();
        connCard.AddView(MakeCardTitle(T("Bridge & GitHub Config", "Ponte Cloudflare & GitHub")));

        relaySummaryTv = new TextView(this) { TextSize = 12 };
        relaySummaryTv.SetTextColor(TextSecondary);
        relaySummaryTv.SetPadding(0, Dp(6), 0, Dp(4));
        connCard.AddView(relaySummaryTv);

        githubSummaryTv = new TextView(this) { TextSize = 12 };
        githubSummaryTv.SetTextColor(TextSecondary);
        githubSummaryTv.SetPadding(0, 0, 0, Dp(12));
        connCard.AddView(githubSummaryTv);

        LinearLayout cfgRow = MakeRow();
        Button cfgRelayBtn = MakeButton(T("Relay & Key", "Worker & Chave"), CardElevated, TextPrimary, false);
        cfgRelayBtn.Click += (_, _) => ShowRelayConfigSheet();
        cfgRow.AddView(cfgRelayBtn, MakeWeightedLp(1f, rightMarginDp: 8));

        Button cfgGhBtn = MakeButton(T("GitHub Push", "GitHub Push"), CardElevated, TextPrimary, false);
        cfgGhBtn.Click += (_, _) => ShowGitHubConfigSheet();
        cfgRow.AddView(cfgGhBtn, MakeWeightedLp(1f));
        connCard.AddView(cfgRow);
        content.AddView(connCard);

        // 4. Live Activity Journal + Kill Switch
        LinearLayout logCard = MakeCard();
        LinearLayout logHeader = MakeRow();
        TextView logTitle = MakeCardTitle(T("Live Agent Activity", "Atividade do Agente"));
        logHeader.AddView(logTitle, MakeWeightedLp(1f));
        Button clearBtn = MakeButton(T("Clear", "Limpar"), CardElevated, TextSecondary, false);
        clearBtn.SetPadding(Dp(12), Dp(4), Dp(12), Dp(4));
        clearBtn.Click += (_, _) => OmniSettingsStore.ClearActivity();
        logHeader.AddView(clearBtn);
        logCard.AddView(logHeader);

        logViewTv = new TextView(this) { TextSize = 11 };
        logViewTv.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        logViewTv.SetTextColor(TextSecondary);
        logViewTv.SetPadding(0, Dp(10), 0, 0);
        logCard.AddView(logViewTv);
        content.AddView(logCard);

        scroll.AddView(content);
        rootFrame.AddView(scroll);

        // Custom One UI 9 Modal Overlay (zero stock AlertDialog!)
        modalOverlay = new FrameLayout(this) {
            Visibility = ViewStates.Gone,
            Clickable = true,
            Focusable = true,
            LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };
        modalOverlay.SetBackgroundColor(Color.Argb(185, 0, 0, 0));
        modalOverlay.Click += (_, _) => HideModal();
        rootFrame.AddView(modalOverlay);

        SetContentView(rootFrame);
    }

    Button MakeModeButton(string label, string modeValue) {
        Button b = MakeButton(label, CardElevated, TextPrimary, false);
        b.Click += (_, _) => {
            store.SecurityMode = modeValue;
            OmniSettingsStore.AppendActivity("SEC", $"Security mode -> {modeValue}");
            RefreshUi();
        };
        return b;
    }

    void ToggleService() {
        if (OmniAgentForegroundService.IsRunning) {
            store.ServiceEnabled = false;
            OmniAgentForegroundService.Stop(this);
        } else {
            if (!store.HasPairSecret || !OmniSettingsStore.TryNormalizeRelayUrl(store.RelayUrl, out _)) {
                ShowRelayConfigSheet();
                return;
            }
            store.ServiceEnabled = true;
            OmniAgentForegroundService.Start(this);
        }
        RefreshUi();
    }

    void RefreshUi() {
        bool running = OmniAgentForegroundService.IsRunning;
        statusBadge.Text = running
            ? T("● ONLINE (channel: android)", "● ONLINE (canal: android)")
            : T("○ STANDBY OFF", "○ DESLIGADO");
        statusBadge.SetTextColor(running ? AccentGreen : TextSecondary);
        statusBadge.Background = Pill(running ? Color.Argb(36, 34, 197, 94) : CardElevated, 20);

        statusSubtitle.Text = running
            ? T(
                $"Listening for encrypted AI Studio commands • {OmniAgentForegroundService.CommandsHandled} handled",
                $"Ouvindo comandos E2E do AI Studio • {OmniAgentForegroundService.CommandsHandled} executado(s)")
            : T(
                "Tap Start Agent to enable remote GitHub push, APK install & Termux wake.",
                "Toque em Ligar Agente para push GitHub, instalar APK e acordar Termux.");

        toggleServiceBtn.Text = running ? T("Stop Agent", "Parar Agente") : T("Start Agent", "Ligar Agente");
        toggleServiceBtn.Background = Pill(running ? AccentRed : AccentBlue, 18);

        string mode = store.SecurityMode;
        StyleModeBtn(modeSmartBtn, mode == "smart");
        StyleModeBtn(modeBioBtn, mode == "biometric");
        StyleModeBtn(modeTrustedBtn, mode == "trusted");

        string pairDisp = store.HasPairSecret ? OmniCrypto.FormatPairKeyDisplay(store.LoadPairSecretHex()) : T("Not paired", "Sem chave");
        relaySummaryTv.Text = $"Worker: {(string.IsNullOrEmpty(store.RelayUrl) ? T("Not set", "Não configurado") : store.RelayUrl)}  •  Key: {pairDisp}";
        githubSummaryTv.Text = $"GitHub: {store.GitHubRepo} ({store.GitHubBranch})  •  PAT: {(store.HasGitHubPat ? T("Encrypted in Keystore", "Salvo no Keystore") : T("Optional", "Opcional"))}";

        List<string> lines = OmniSettingsStore.GetActivitySnapshot();
        logViewTv.Text = lines.Count == 0
            ? T("No commands yet. Start the agent or wake Termux daemon.", "Nenhum comando ainda. Ligue o agente ou inicie o daemon no Termux.")
            : string.Join("\n", lines.Take(18));
    }

    void StyleModeBtn(Button btn, bool selected) {
        btn.Background = Pill(selected ? AccentBlue : CardElevated, 14, selected ? null : StrokeColor);
        btn.SetTextColor(selected ? TextPrimary : TextSecondary);
    }

    void ShowRelayConfigSheet() {
        LinearLayout sheet = CreateSheet(T("Cloudflare Relay & Pairing Key", "Cloudflare Worker & Chave de Pareamento"));
        EditText urlInput = MakeInput(T("https://cavernize-diag-relay.xxx.workers.dev", "https://cavernize-diag-relay.xxx.workers.dev"), store.RelayUrl, false);
        EditText keyInput = MakeInput(T("Paste same 64-hex CAVPAIR key from Cavernize", "Cole a mesma chave CAVPAIR (64 hex) do Cavernize"), store.LoadPairSecretHex(), false);

        sheet.AddView(MakeFieldLabel("Cloudflare Worker URL"));
        sheet.AddView(urlInput);
        sheet.AddView(MakeFieldLabel(T("256-bit Pairing Key (AES-256-GCM Keystore)", "Chave 256-bit (Keystore AES-256-GCM)")));
        sheet.AddView(keyInput);

        LinearLayout btnRow = MakeRow();
        btnRow.SetPadding(0, Dp(14), 0, 0);
        Button genBtn = MakeButton(T("Generate", "Gerar"), CardElevated, TextPrimary, false);
        genBtn.Click += (_, _) => keyInput.Text = OmniCrypto.GeneratePairSecretHex();
        btnRow.AddView(genBtn, MakeWeightedLp(1f, rightMarginDp: 8));

        Button saveBtn = MakeButton(T("Save", "Salvar"), AccentBlue, TextPrimary, true);
        saveBtn.Click += (_, _) => {
            if (!OmniSettingsStore.TryNormalizeRelayUrl(urlInput.Text, out string normUrl)) {
                Toast.MakeText(this, T("Invalid https:// Worker URL", "URL https:// inválida"), ToastLength.Short)?.Show();
                return;
            }
            string normKey = OmniCrypto.NormalizePairSecretHex(keyInput.Text);
            if (string.IsNullOrEmpty(normKey)) {
                Toast.MakeText(this, T("Key must be 64 hex chars", "Chave precisa ter 64 caracteres hex"), ToastLength.Short)?.Show();
                return;
            }
            store.RelayUrl = normUrl;
            store.SavePairSecretHex(normKey);
            OmniSettingsStore.AppendActivity("CFG", "Saved Cloudflare Worker URL & Keystore pairing key.");
            HideModal();
            RefreshUi();
        };
        btnRow.AddView(saveBtn, MakeWeightedLp(1f));
        sheet.AddView(btnRow);
        PresentSheet(sheet);
    }

    void ShowGitHubConfigSheet() {
        LinearLayout sheet = CreateSheet(T("Direct GitHub Push (Optional)", "GitHub Push Direto (Opcional)"));
        EditText repoInput = MakeInput("guumachadooo/Cavern", store.GitHubRepo, false);
        EditText branchInput = MakeInput("master", store.GitHubBranch, false);
        EditText patInput = MakeInput(T("Fine-Grained PAT (Contents: Read & Write)", "Fine-Grained PAT (Contents: Read & Write)"), "", true);

        sheet.AddView(MakeFieldLabel(T("Repository (owner/repo)", "Repositório (dono/repo)")));
        sheet.AddView(repoInput);
        sheet.AddView(MakeFieldLabel("Branch"));
        sheet.AddView(branchInput);
        sheet.AddView(MakeFieldLabel(T("GitHub PAT (leave empty to keep current)", "GitHub PAT (deixe vazio para manter atual)")));
        sheet.AddView(patInput);

        LinearLayout btnRow = MakeRow();
        btnRow.SetPadding(0, Dp(14), 0, 0);
        Button cancelBtn = MakeButton(T("Cancel", "Cancelar"), CardElevated, TextSecondary, false);
        cancelBtn.Click += (_, _) => HideModal();
        btnRow.AddView(cancelBtn, MakeWeightedLp(1f, rightMarginDp: 8));

        Button saveBtn = MakeButton(T("Save", "Salvar"), AccentBlue, TextPrimary, true);
        saveBtn.Click += (_, _) => {
            store.GitHubRepo = repoInput.Text;
            store.GitHubBranch = branchInput.Text;
            if (!string.IsNullOrWhiteSpace(patInput.Text)) {
                store.SaveGitHubPat(patInput.Text);
            }
            OmniSettingsStore.AppendActivity("CFG", $"Updated GitHub config ({store.GitHubRepo}@{store.GitHubBranch}).");
            HideModal();
            RefreshUi();
        };
        btnRow.AddView(saveBtn, MakeWeightedLp(1f));
        sheet.AddView(btnRow);
        PresentSheet(sheet);
    }

    LinearLayout CreateSheet(string titleText) {
        LinearLayout sheet = new(this) {
            Orientation = Orientation.Vertical,
            Clickable = true,
            Background = Pill(CardBg, 26, StrokeColor)
        };
        sheet.SetPadding(Dp(20), Dp(20), Dp(20), Dp(22));
        TextView t = new(this) { Text = titleText, TextSize = 17 };
        t.SetTextColor(TextPrimary);
        t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        t.SetPadding(0, 0, 0, Dp(12));
        sheet.AddView(t);
        return sheet;
    }

    void PresentSheet(LinearLayout sheet) {
        modalOverlay.RemoveAllViews();
        FrameLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom
        };
        lp.SetMargins(Dp(12), Dp(12), Dp(12), Dp(20));
        modalOverlay.AddView(sheet, lp);
        modalOverlay.Visibility = ViewStates.Visible;
    }

    void HideModal() {
        modalOverlay.Visibility = ViewStates.Gone;
        modalOverlay.RemoveAllViews();
    }

    LinearLayout MakeCard() {
        LinearLayout card = new(this) {
            Orientation = Orientation.Vertical,
            Background = Pill(CardBg, 24, StrokeColor)
        };
        card.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.SetMargins(0, 0, 0, Dp(14));
        card.LayoutParameters = lp;
        return card;
    }

    LinearLayout MakeRow() {
        LinearLayout row = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
    }

    LinearLayout.LayoutParams MakeWeightedLp(float weight, int leftMarginDp = 0, int rightMarginDp = 0) {
        LinearLayout.LayoutParams lp = new(0, ViewGroup.LayoutParams.WrapContent, weight);
        lp.SetMargins(Dp(leftMarginDp), 0, Dp(rightMarginDp), 0);
        return lp;
    }

    TextView MakeCardTitle(string text) {
        TextView tv = new(this) { Text = text, TextSize = 15 };
        tv.SetTextColor(TextPrimary);
        tv.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        return tv;
    }

    TextView MakeFieldLabel(string text) {
        TextView tv = new(this) { Text = text, TextSize = 12 };
        tv.SetTextColor(TextSecondary);
        tv.SetPadding(0, Dp(8), 0, Dp(4));
        return tv;
    }

    EditText MakeInput(string hint, string initial, bool password) {
        EditText et = new(this) {
            Hint = hint,
            Text = initial ?? "",
            TextSize = 13,
            Background = Pill(CardElevated, 14, StrokeColor),
            InputType = password
                ? (InputTypes.ClassText | InputTypes.TextVariationPassword)
                : (InputTypes.ClassText | InputTypes.TextFlagNoSuggestions)
        };
        et.SetTextColor(TextPrimary);
        et.SetHintTextColor(TextSecondary);
        et.SetSingleLine(true);
        et.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        return et;
    }

    Button MakeButton(string text, Color bg, Color fg, bool bold) {
        Button b = new(this) {
            Text = text,
            TextSize = 13,
            Background = Pill(bg, 16)
        };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = TextUtils.TruncateAt.End;
        b.SetTextColor(fg);
        b.SetTypeface(bold ? Typeface.DefaultBold : Typeface.Default, bold ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        b.SetPadding(Dp(14), Dp(11), Dp(14), Dp(11));
        b.SetMinHeight(Dp(42));
        return b;
    }
}
