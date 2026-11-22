using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Android.App;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

/// <summary>
/// Live UI, Modal, Interactive Control &amp; Choreographer Latency/Frame-Jank Self-Audit Engine
/// (Layers L8, L9, L10, L12 of <see cref="SelfDiagnosticEngine"/>). Walks all 5 tabs, 6 Settings categories,
/// 27 real modals, and live interactive controls on the Android UI thread while sampling real Android
/// <see cref="Choreographer"/> vsync frames to catch main-thread stalls, slow screen opens, dropped frames,
/// text/NaN leaks, and layout defects, then cleanly restores the user's exact navigation state.
/// </summary>
public partial class MainActivity {
    static readonly Regex UnformattedPlaceholderRegex = new(@"\{\d+(?::[^}]+)?\}", RegexOptions.Compiled);
    static readonly Regex CamelKeyCandidateRegex = new(@"^[A-Z][a-z]+[A-Z][A-Za-z0-9]+$", RegexOptions.Compiled);

    sealed class UiAuditAccumulator {
        public int ViewsInspected;
        public int ButtonsInspected;
        public int SelectorsInspected;
        public readonly List<string> Failures = [];
        public readonly List<string> Warnings = [];
    }

    /// <summary>
    /// Real Android Choreographer vsync frame sampler measuring inter-frame gaps and dropped frames (&gt; 20 ms)
    /// over a bounded observation window on the UI thread.
    /// </summary>
    sealed class UiFrameJankSampler : Java.Lang.Object, Choreographer.IFrameCallback {
        readonly TaskCompletionSource<(long firstFrameMs, double worstFrameMs, int droppedFrames)> tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly long startTimestamp;
        readonly int targetFrames;
        long prevFrameNanos;
        long firstFrameElapsedMs = -1;
        double worstGapMs;
        int droppedFrames;
        int framesSeen;
        bool finished;

        public UiFrameJankSampler(long startTimestamp, int targetFrames = 3) {
            this.startTimestamp = startTimestamp;
            this.targetFrames = Math.Max(1, targetFrames);
        }

        public Task<(long firstFrameMs, double worstFrameMs, int droppedFrames)> Task => tcs.Task;

        public void StartOnUiThread() {
            Choreographer.Instance?.PostFrameCallback(this);
        }

        public void DoFrame(long frameTimeNanos) {
            if (finished) {
                return;
            }
            framesSeen++;
            if (firstFrameElapsedMs < 0) {
                firstFrameElapsedMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            } else {
                double gapMs = (frameTimeNanos - prevFrameNanos) / 1_000_000.0;
                if (gapMs > worstGapMs) {
                    worstGapMs = gapMs;
                }
                if (gapMs > 20.5) { // Missed 60 Hz frame budget (16.67 ms + jitter margin)
                    droppedFrames += Math.Max(1, (int)Math.Floor(gapMs / 16.67) - 1);
                }
            }
            prevFrameNanos = frameTimeNanos;

            if (framesSeen >= targetFrames) {
                finished = true;
                tcs.TrySetResult((firstFrameElapsedMs, Math.Round(worstGapMs, 1), droppedFrames));
            } else {
                Choreographer.Instance?.PostFrameCallback(this);
            }
        }
    }

    static (string tier, int warnBudgetMs, int stallFailMs) GetUiPerfBudget(string targetName) {
        if (targetName is "Tab1:3DRoom" or "Modal:AutoEqProfileSelector") {
            return ("Heavy", 350, 800);
        }
        if (targetName is "Tab0:Player" ||
            targetName.StartsWith("SettingsCat", StringComparison.Ordinal) ||
            targetName is "Modal:BinauralProfileSelector" or "Modal:PerceptualHrtfWizard" or "Modal:NeuralModels"
                or "Modal:AutoVocalMixMatch" or "Modal:Scarlett2i2Controls" or "Modal:StudioBatch"
                or "Modal:OnlineMetadata" or "Modal:TrackSelector" or "Modal:SmartCleanInspector") {
            return ("Medium", 180, 420);
        }
        return ("Simple", 90, 240);
    }

    async Task<UiPerfSample> MeasureUiTransitionAsync(
        string targetName,
        Action uiTransition,
        CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        long startStamp = Stopwatch.GetTimestamp();
        UiFrameJankSampler sampler = new(startStamp, targetFrames: 3);

        long uiBlockMs = await RunOnUiThreadWithTimeoutAsync(() => {
            Stopwatch sw = Stopwatch.StartNew();
            uiTransition();
            sw.Stop();
            sampler.StartOnUiThread();
            return sw.ElapsedMilliseconds;
        }, 4000).ConfigureAwait(false);

        // Wait for 3 real Choreographer vsync frames (or bounded timeout when screen is off/headless)
        Task timeoutTask = Task.Delay(350, ct);
        Task completed = await Task.WhenAny(sampler.Task, timeoutTask).ConfigureAwait(false);
        long firstFrameMs;
        double worstFrameMs;
        int dropped;
        if (completed == sampler.Task) {
            (firstFrameMs, worstFrameMs, dropped) = await sampler.Task.ConfigureAwait(false);
        } else {
            firstFrameMs = Math.Max(uiBlockMs, (long)Stopwatch.GetElapsedTime(startStamp).TotalMilliseconds);
            worstFrameMs = uiBlockMs;
            dropped = uiBlockMs > 34 ? (int)(uiBlockMs / 16.67) : 0;
        }

        (string tier, int warnBudgetMs, int stallFailMs) = GetUiPerfBudget(targetName);
        string status;
        if (uiBlockMs >= stallFailMs || firstFrameMs >= stallFailMs || worstFrameMs >= 250.0) {
            status = "STALL";
        } else if (uiBlockMs > warnBudgetMs || firstFrameMs > warnBudgetMs || dropped >= 4) {
            status = "SLOW";
        } else {
            status = "OK";
        }

        return new UiPerfSample {
            Target = targetName,
            Complexity = tier,
            BudgetMs = warnBudgetMs,
            UiThreadBlockMs = uiBlockMs,
            FirstFrameMs = firstFrameMs,
            WorstFrameMs = worstFrameMs,
            DroppedFrames = dropped,
            Status = status
        };
    }

    async Task<(List<SelfCheckItem> Checks, List<UiPerfSample> PerfSamples)> RunLiveUiSelfAuditInternalAsync(
        Action<string> onProgress, CancellationToken ct) {
        List<SelfCheckItem> results = [];
        List<UiPerfSample> perfSamples = [];
        if (uiDestroyed || IsFinishing) {
            results.Add(new SelfCheckItem {
                Id = "ui.host_attached",
                Layer = "L8-LiveUI",
                Name = "Live UI Audit",
                Status = SelfCheckStatus.Skip,
                ElapsedMs = 0,
                Details = "MainActivity is not attached or finishing."
            });
            return (results, perfSamples);
        }

        // Snapshot user navigation state so everything restores identically in finally
        (int savedTab, int savedCat, bool savedPro, string savedSearch) = await RunOnUiThreadWithTimeoutAsync(() =>
            (activeTab, activeSettingsCategory, isSettingsProMode, settingsSearchQuery ?? ""), 4000).ConfigureAwait(false);

        try {
            // ==================== L8: 5 TABS + 6 SETTINGS CATEGORIES GEOMETRY, TEXT & LATENCY AUDIT ====================
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke("L8/L12: Auditing 5 Tabs & 6 Settings Categories + Choreographer Latency");
            Stopwatch swL8 = Stopwatch.StartNew();
            UiAuditAccumulator accL8 = new();

            string[] tabLabels = ["Tab0:Player", "Tab1:3DRoom", "Tab2:Studio", "Tab3:Stems", "Tab4:SettingsHub"];
            for (int t = 0; t <= 4; t++) {
                ct.ThrowIfCancellationRequested();
                int tabIdx = t;
                UiPerfSample perf = await MeasureUiTransitionAsync(tabLabels[tabIdx], () => {
                    SwitchTab(tabIdx);
                    if (tabIdx == 4) {
                        ClearSettingsSearch();
                        OpenSettingsCategory(-1);
                    }
                }, ct).ConfigureAwait(false);
                perfSamples.Add(perf);

                await RunOnUiThreadWithTimeoutAsync(() => {
                    AuditViewTree(rootFrameHost ?? Window?.DecorView, tabLabels[tabIdx], accL8, checkZeroSize: false);
                    return true;
                }, 4000).ConfigureAwait(false);
            }

            // Walk all 6 Settings categories in Pro mode so every hidden/pro card is audited too
            await RunOnUiThreadWithTimeoutAsync(() => {
                SwitchTab(4);
                SetSettingsProMode(true);
                ClearSettingsSearch();
                return true;
            }, 4000).ConfigureAwait(false);

            for (int cat = 0; cat < 6; cat++) {
                ct.ThrowIfCancellationRequested();
                int catIdx = cat;
                string catLabel = $"SettingsCat{catIdx}";
                UiPerfSample perf = await MeasureUiTransitionAsync(catLabel, () => {
                    OpenSettingsCategory(catIdx);
                }, ct).ConfigureAwait(false);
                perfSamples.Add(perf);

                await RunOnUiThreadWithTimeoutAsync(() => {
                    AuditViewTree((View)settingsCardsContainer ?? rootFrameHost, catLabel, accL8, checkZeroSize: false);
                    return true;
                }, 4000).ConfigureAwait(false);
            }
            swL8.Stop();

            SelfCheckStatus stL8 = accL8.Failures.Count > 0
                ? SelfCheckStatus.Fail
                : (accL8.Warnings.Count > 0 ? SelfCheckStatus.Warn : SelfCheckStatus.Pass);
            string detL8 = accL8.Failures.Count > 0
                ? $"{accL8.Failures.Count} UI defect(s), {accL8.Warnings.Count} warn(s) across {accL8.ViewsInspected} views: {string.Join("; ", accL8.Failures.Take(8))}"
                : (accL8.Warnings.Count > 0
                    ? $"0 fatal UI defects, {accL8.Warnings.Count} layout/click warn(s) across {accL8.ViewsInspected} views: {string.Join("; ", accL8.Warnings.Take(8))}"
                    : $"All 5 tabs + 6 Settings categories clean ({accL8.ViewsInspected} views, {accL8.ButtonsInspected} buttons, {accL8.SelectorsInspected} selectors, 0 NaN/leaks/clipping)");
            results.Add(new SelfCheckItem {
                Id = "ui.tabs_and_settings",
                Layer = "L8-LiveUI",
                Name = "5 Tabs & 6 Settings Categories Audit",
                Status = stL8,
                ElapsedMs = swL8.ElapsedMilliseconds,
                Details = detL8
            });

            // ==================== L9: EXHAUSTIVE LIVE MODAL SMOKE + OPEN-LATENCY PROFILING ====================
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke("L9/L12: Live Modal Smoke & Open-Latency Audit (27 Modals)");
            Stopwatch swL9 = Stopwatch.StartNew();
            UiAuditAccumulator accL9 = new();

            (string name, Action opener)[] modals = [
                ("BinauralProfileSelector", ShowBinauralProfileSelectorDialog),
                ("BinauralCredits", ShowBinauralLicenseCreditsDialog),
                ("AutoEqProfileSelector", ShowAutoEqProfileSelectorDialog),
                ("AutoEqCredits", ShowAutoEqLicenseCreditsDialog),
                ("PerceptualHrtfWizard", ShowPerceptualHrtfWizardDialog),
                ("OscPort", ShowOscPortModal),
                ("NeuralModels", ShowNeuralModelsModal),
                ("ImportCustomOnnx", ShowImportCustomOnnxDialog),
                ("LgsrLicenses", ShowLgsrLicensesModal),
                ("AutoVocalMixMatch", ShowAutoVocalMixMatchModal),
                ("CalibrateRtt", ShowCalibrateRttModal),
                ("Scarlett2i2Controls", ShowScarlett2i2ControlsModal),
                ("ProAudioCredits", ShowProAudioCreditsModal),
                ("LiveBridgeConfig", ShowLiveBridgeConfigModal),
                ("CloudLogSync", ShowCloudLogSyncModal),
                ("RenderTarget", ShowRenderTargetDialog),
                ("Metadata", ShowMetadataDialog),
                ("StudioBatch", ShowStudioW12BatchDialog),
                ("AudioSpec", ShowAudioSpecDialog),
                ("LibraryQualityFilter", ShowLibraryQualityFilterDialog),
                ("LibrarySort", ShowLibrarySortDialog),
                ("SmartCleanInspector", ShowSmartCleanInspectorModal),
                ("LibraryFolderFilter", ShowLibraryFolderFilterDialog),
                ("OnlineMetadata", ShowOnlineMetadataDialog),
                ("Subtitles", ShowSubtitlesDialog),
                ("TrackSelector", ShowTrackSelectorDialog),
                ("TechnicalStream", ShowTechnicalStreamDialog)
            ];

            int openedCount = 0;
            foreach ((string modalName, Action opener) in modals) {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke($"L9/L12 Modal: {modalName}");
                try {
                    UiPerfSample perf = await MeasureUiTransitionAsync($"Modal:{modalName}", () => {
                        OneUiModalDialog.DismissAllActiveDialogs();
                        opener();
                    }, ct).ConfigureAwait(false);
                    perfSamples.Add(perf);

                    await RunOnUiThreadWithTimeoutAsync(() => {
                        IReadOnlyList<Dialog> liveDlgs = OneUiModalDialog.GetActiveDialogs();
                        if (liveDlgs.Count > 0) {
                            openedCount++;
                            for (int i = 0; i < liveDlgs.Count; i++) {
                                View decor = liveDlgs[i]?.Window?.DecorView;
                                if (decor != null) {
                                    AuditViewTree(decor, $"Modal:{modalName}", accL9, checkZeroSize: false);
                                }
                            }
                        }
                        OneUiModalDialog.DismissAllActiveDialogs();
                        return true;
                    }, 4000).ConfigureAwait(false);
                } catch (System.OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    accL9.Failures.Add($"{modalName} threw {ex.GetType().Name}: {ex.Message}");
                    try {
                        await RunOnUiThreadWithTimeoutAsync(() => {
                            OneUiModalDialog.DismissAllActiveDialogs();
                            return true;
                        }, 2000).ConfigureAwait(false);
                    } catch {
                        // Best effort cleanup
                    }
                }
            }
            swL9.Stop();

            SelfCheckStatus stL9 = accL9.Failures.Count > 0
                ? SelfCheckStatus.Fail
                : (accL9.Warnings.Count > 0 ? SelfCheckStatus.Warn : SelfCheckStatus.Pass);
            string detL9 = accL9.Failures.Count > 0
                ? $"{accL9.Failures.Count} modal crash/defect(s) ({openedCount}/{modals.Length} opened): {string.Join("; ", accL9.Failures.Take(8))}"
                : (accL9.Warnings.Count > 0
                    ? $"{openedCount}/{modals.Length} modals clean-open, {accL9.Warnings.Count} warn(s): {string.Join("; ", accL9.Warnings.Take(8))}"
                    : $"All {modals.Length} modals probed cleanly ({openedCount} OneUiModalDialog rendered, {accL9.ViewsInspected} views, 0 crashes/NaN/leaks)");
            results.Add(new SelfCheckItem {
                Id = "ui.modals_smoke",
                Layer = "L9-Modals",
                Name = $"Live Modal Smoke Test ({modals.Length} Dialogs)",
                Status = stL9,
                ElapsedMs = swL9.ElapsedMilliseconds,
                Details = detL9
            });

            // ==================== L10: INTERACTIVE CONTROLS & STATE-TRANSITION STRESS ====================
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke("L10: Testing Interactive Controls & State Refresh");
            Stopwatch swL10 = Stopwatch.StartNew();
            List<string> ctrlFails = [];
            List<string> ctrlWarns = [];
            int selectorChecks = 0;

            await RunOnUiThreadWithTimeoutAsync(() => {
                SwitchTab(4);
                OpenSettingsCategory(-1);

                // 1. Settings search filter -> verify non-empty matches -> clear
                if (settingsSearchInput != null) {
                    settingsSearchInput.Text = "binaural";
                    int visibleMatches = settingsCardsRegistry.Count(e => e.Card != null && e.Card.Visibility == ViewStates.Visible);
                    if (visibleMatches == 0) {
                        ctrlFails.Add("Settings search 'binaural' matched 0 cards");
                    }
                    ClearSettingsSearch();
                    if (!string.IsNullOrEmpty(settingsSearchQuery)) {
                        ctrlFails.Add("ClearSettingsSearch did not clear query");
                    }
                } else {
                    ctrlWarns.Add("settingsSearchInput null");
                }

                // 2. Pro mode toggle round-trip
                bool beforePro = isSettingsProMode;
                SetSettingsProMode(!beforePro);
                if (isSettingsProMode == beforePro) {
                    ctrlFails.Add("SetSettingsProMode failed to toggle state");
                }
                SetSettingsProMode(beforePro);

                // 3. Verify all OneUiInlineSelector controls in settings tree have valid items & selection
                void InspectSelectors(View v) {
                    if (v == null) return;
                    if (v is OneUiInlineSelector sel) {
                        selectorChecks++;
                        if (sel.ItemCount <= 0) {
                            ctrlWarns.Add("OneUiInlineSelector has 0 items");
                        } else if (sel.SelectedIndex < 0 || sel.SelectedIndex >= sel.ItemCount) {
                            ctrlFails.Add($"OneUiInlineSelector out-of-range index {sel.SelectedIndex}/{sel.ItemCount}");
                        }
                    }
                    if (v is ViewGroup vg) {
                        for (int i = 0; i < vg.ChildCount; i++) {
                            InspectSelectors(vg.GetChildAt(i));
                        }
                    }
                }
                InspectSelectors(rootFrameHost);

                // 4. Full UI refresh stress (must never throw)
                RefreshAllUiState();
                RefreshSettingsTabButtons();
                RefreshLogSummaryLabel();
                return true;
            }, 6000).ConfigureAwait(false);
            swL10.Stop();

            SelfCheckStatus stL10 = ctrlFails.Count > 0
                ? SelfCheckStatus.Fail
                : (ctrlWarns.Count > 0 ? SelfCheckStatus.Warn : SelfCheckStatus.Pass);
            string detL10 = ctrlFails.Count > 0
                ? $"Control failures: {string.Join("; ", ctrlFails)}"
                : (ctrlWarns.Count > 0
                    ? $"Controls OK with warns: {string.Join("; ", ctrlWarns)}"
                    : $"Settings search, Pro Mode toggle, {selectorChecks} inline selectors & full RefreshAllUiState passed");
            results.Add(new SelfCheckItem {
                Id = "ui.interactive_controls",
                Layer = "L10-Controls",
                Name = "Interactive Controls & Refresh Audit",
                Status = stL10,
                ElapsedMs = swL10.ElapsedMilliseconds,
                Details = detL10
            });

            // ==================== L12: UI LATENCY & CHOREOGRAPHER FRAME-JANK SUMMARY ====================
            List<UiPerfSample> stalls = perfSamples.Where(p => p.Status == "STALL").OrderByDescending(p => p.FirstFrameMs).ToList();
            List<UiPerfSample> slows = perfSamples.Where(p => p.Status == "SLOW").OrderByDescending(p => p.FirstFrameMs).ToList();
            long[] sortedMs = perfSamples.Select(p => p.FirstFrameMs).OrderBy(x => x).ToArray();
            long p50 = sortedMs.Length > 0 ? sortedMs[sortedMs.Length / 2] : 0;
            long p95 = sortedMs.Length > 0 ? sortedMs[(int)Math.Min(sortedMs.Length - 1, Math.Ceiling(sortedMs.Length * 0.95) - 1)] : 0;
            int totalDrops = perfSamples.Sum(p => p.DroppedFrames);
            UiPerfSample worst = perfSamples.OrderByDescending(p => p.FirstFrameMs).FirstOrDefault();

            SelfCheckStatus stL12 = stalls.Count > 0
                ? SelfCheckStatus.Fail
                : (slows.Count > 0 ? SelfCheckStatus.Warn : SelfCheckStatus.Pass);
            string detL12;
            if (stalls.Count > 0) {
                string topStalls = string.Join(", ", stalls.Take(5).Select(s => $"{s.Target}({s.UiThreadBlockMs}ms/1st={s.FirstFrameMs}ms>{s.BudgetMs}ms)"));
                detL12 = $"{stalls.Count} UI stall(s), {slows.Count} slow ({perfSamples.Count} targets, P50={p50}ms, P95={p95}ms, drops={totalDrops}): {topStalls}";
            } else if (slows.Count > 0) {
                string topSlows = string.Join(", ", slows.Take(5).Select(s => $"{s.Target}({s.UiThreadBlockMs}ms/1st={s.FirstFrameMs}ms>{s.BudgetMs}ms)"));
                detL12 = $"0 stalls, {slows.Count} slower-than-budget target(s) ({perfSamples.Count} profiled, P50={p50}ms, P95={p95}ms, drops={totalDrops}): {topSlows}";
            } else {
                string worstDesc = worst != null ? $"{worst.Target}={worst.FirstFrameMs}ms" : "n/a";
                detL12 = string.Format(
                    CultureInfo.InvariantCulture,
                    "All {0} screens/modals within latency budget (P50={1}ms, P95={2}ms, max={3}, drops={4})",
                    perfSamples.Count, p50, p95, worstDesc, totalDrops);
            }
            results.Add(new SelfCheckItem {
                Id = "ui.perf_latency_jank",
                Layer = "L12-PerfJank",
                Name = "UI Open Latency & Choreographer Frame-Jank Profiler",
                Status = stL12,
                ElapsedMs = perfSamples.Sum(p => p.FirstFrameMs),
                Details = detL12
            });
        } finally {
            // Guaranteed restore of the user's exact screen state
            try {
                await RunOnUiThreadWithTimeoutAsync(() => {
                    OneUiModalDialog.DismissAllActiveDialogs();
                    SetSettingsProMode(savedPro);
                    if (string.IsNullOrEmpty(savedSearch)) {
                        ClearSettingsSearch();
                    } else if (settingsSearchInput != null) {
                        settingsSearchInput.Text = savedSearch;
                    }
                    OpenSettingsCategory(savedCat);
                    SwitchTab(savedTab);
                    RefreshLogSummaryLabel();
                    return true;
                }, 4000).ConfigureAwait(false);
            } catch {
                // Best effort restore
            }
        }

        return (results, perfSamples);
    }

    void AuditViewTree(View root, string scopeName, UiAuditAccumulator acc, bool checkZeroSize) {
        if (root == null) {
            return;
        }
        int minButtonPx = Dp(18);

        void Walk(View v, int depth) {
            if (v == null || depth > 24 || v.Visibility != ViewStates.Visible) {
                return;
            }
            acc.ViewsInspected++;

            if (v is OneUiInlineSelector) {
                acc.SelectorsInspected++;
            }

            if (v is TextView tv) {
                string txt = tv.Text ?? "";
                if (!string.IsNullOrEmpty(txt)) {
                    // Check raw unformatted placeholders like "{0}" or "{1:0.0}"
                    if (UnformattedPlaceholderRegex.IsMatch(txt)) {
                        acc.Failures.Add($"{scopeName}: unformatted placeholder in '{TruncateAuditText(txt)}'");
                    }
                    // Check NaN / Infinity leaks (whole token)
                    if (txt.Contains("NaN", StringComparison.Ordinal) || txt.Contains("Infinity", StringComparison.Ordinal)) {
                        acc.Failures.Add($"{scopeName}: NaN/Infinity text leak in '{TruncateAuditText(txt)}'");
                    }
                    // Check raw leaked AndroidStrings PascalCase key (e.g. "CloudLogUploadBtn")
                    string trimmed = txt.Trim();
                    if (trimmed.Length >= 8 && CamelKeyCandidateRegex.IsMatch(trimmed) && AndroidStrings.IsKnownKey(trimmed)) {
                        acc.Failures.Add($"{scopeName}: untranslated string key '{trimmed}'");
                    }
                }
            }

            if (v is Button btn && btn is not CompoundButton) {
                acc.ButtonsInspected++;
                if (btn.Height > 0 && btn.Height < minButtonPx) {
                    acc.Warnings.Add($"{scopeName}: clipped button '{TruncateAuditText(btn.Text)}' (h={btn.Height}px)");
                }
                if (checkZeroSize && btn.Width == 0 && btn.Height == 0) {
                    acc.Warnings.Add($"{scopeName}: zero-size visible button '{TruncateAuditText(btn.Text)}'");
                }
                if (btn.Clickable && btn.Enabled && !btn.HasOnClickListeners) {
                    acc.Warnings.Add($"{scopeName}: button without click listener '{TruncateAuditText(btn.Text)}'");
                }
            }

            // Detect horizontal LinearLayouts with >=2 visible Buttons missing BaselineAligned = false (BL-013)
            if (v is LinearLayout ll && ll.Orientation == Orientation.Horizontal && ll.BaselineAligned) {
                int visBtnCount = 0;
                for (int i = 0; i < ll.ChildCount; i++) {
                    View ch = ll.GetChildAt(i);
                    if (ch is Button b && b is not CompoundButton && b.Visibility == ViewStates.Visible) {
                        visBtnCount++;
                    }
                }
                if (visBtnCount >= 2) {
                    acc.Warnings.Add($"{scopeName}: horizontal button row missing BaselineAligned=false ({visBtnCount} btns)");
                }
            }

            if (v is ViewGroup vg) {
                for (int i = 0; i < vg.ChildCount; i++) {
                    Walk(vg.GetChildAt(i), depth + 1);
                }
            }
        }

        Walk(root, 0);
    }

    static string TruncateAuditText(string s) {
        if (string.IsNullOrEmpty(s)) {
            return "";
        }
        string oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 38 ? oneLine : oneLine[..38] + "...";
    }
}
