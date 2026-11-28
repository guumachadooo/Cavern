using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Android.App;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

/// <summary>
/// Live UI, Modal, Interactive Control &amp; Choreographer Latency/Frame-Jank Self-Audit Engine
/// (Layers L8, L9, L10, L12 of <see cref="SelfDiagnosticEngine"/>). Walks all 3 tabs, 4 Sound categories,
/// NowPlaying sheet sub-views, 27 real modals, and live interactive controls on the Android UI thread while sampling real Android
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
        if (targetName is "NowPlaying:3DStage" or "Modal:AutoEqProfileSelector") {
            return ("Heavy", 350, 800);
        }
        if (targetName is "Tab0:Library" or "Tab1:Sound" or "Tab2:Export" ||
            targetName.StartsWith("NowPlaying:", StringComparison.Ordinal) ||
            targetName.StartsWith("SettingsCat", StringComparison.Ordinal) ||
            targetName is "Modal:BinauralProfileSelector" or "Modal:PerceptualHrtfWizard" or "Modal:NeuralModels"
                or "Modal:AutoVocalMixMatch" or "Modal:Scarlett2i2Controls" or "Modal:StudioBatch"
                or "Modal:OnlineMetadata" or "Modal:TrackSelector" or "Modal:SmartCleanInspector"
                or "Modal:LiveBridgeConfig") {
            return ("Medium", 180, 420);
        }
        return ("Simple", 90, 240);
    }

    async Task<(UiPerfSample Sample, bool VsyncSampled)> MeasureUiTransitionAsync(
        string targetName,
        Action uiTransition,
        CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        long startStamp = Stopwatch.GetTimestamp();
        UiFrameJankSampler sampler = new(startStamp, targetFrames: 3);

        (long uiBlockMs, bool canSampleVsync) = await RunOnUiThreadWithTimeoutAsync(() => {
            Stopwatch sw = Stopwatch.StartNew();
            uiTransition();
            sw.Stop();
            bool visible = isActivityResumed && !uiDestroyed && Window?.DecorView?.WindowVisibility == ViewStates.Visible;
            if (visible) {
                sampler.StartOnUiThread();
            }
            return (sw.ElapsedMilliseconds, visible);
        }, 4000).ConfigureAwait(false);

        long firstFrameMs;
        double worstFrameMs;
        int dropped;
        bool vsyncSampled = false;
        if (canSampleVsync) {
            // Wait for 3 real Choreographer vsync frames (bounded timeout if screen turns off mid-audit)
            Task timeoutTask = Task.Delay(350, ct);
            Task completed = await Task.WhenAny(sampler.Task, timeoutTask).ConfigureAwait(false);
            if (completed == sampler.Task) {
                (firstFrameMs, worstFrameMs, dropped) = await sampler.Task.ConfigureAwait(false);
                vsyncSampled = true;
            } else {
                // Screen/vsync paused mid-transition: evaluate using true main-thread block time without adding timeout penalty
                firstFrameMs = uiBlockMs;
                worstFrameMs = uiBlockMs;
                dropped = uiBlockMs > 34 ? (int)(uiBlockMs / 16.67) : 0;
            }
        } else {
            // Headless/background Live Bridge run: Android pauses Choreographer vsync; measure real UI-thread block directly
            firstFrameMs = uiBlockMs;
            worstFrameMs = uiBlockMs;
            dropped = uiBlockMs > 34 ? (int)(uiBlockMs / 16.67) : 0;
        }

        (string tier, int warnBudgetMs, int stallFailMs) = GetUiPerfBudget(targetName);
        string status;
        if (uiBlockMs >= stallFailMs || firstFrameMs >= stallFailMs || (vsyncSampled && worstFrameMs >= 250.0)) {
            status = "STALL";
        } else if (uiBlockMs > warnBudgetMs || firstFrameMs > warnBudgetMs || (vsyncSampled && dropped >= 4)) {
            status = "SLOW";
        } else {
            status = "OK";
        }

        UiPerfSample sample = new() {
            Target = targetName,
            Complexity = tier,
            BudgetMs = warnBudgetMs,
            UiThreadBlockMs = uiBlockMs,
            FirstFrameMs = firstFrameMs,
            WorstFrameMs = worstFrameMs,
            DroppedFrames = dropped,
            Status = status
        };
        return (sample, vsyncSampled);
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
        (int savedTab, int savedCat, bool savedPro, string savedSearch, bool savedNowPlaying, int savedNowPlayingSub) =
            await RunOnUiThreadWithTimeoutAsync(() =>
                (activeTab, activeSettingsCategory, isSettingsProMode, settingsSearchQuery ?? "", isNowPlayingSheetOpen, activeNowPlayingSubView), 4000).ConfigureAwait(false);

        isRunningUiSelfAudit = true;
        int vsyncSampledCount = 0;
        try {
            // ==================== L8: 3 TABS + 4 SOUND CATEGORIES + NOW PLAYING SHEET GEOMETRY, TEXT & LATENCY AUDIT ====================
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke("L8/L12: Auditing 3 Tabs & 4 Sound Categories + Choreographer Latency");
            Stopwatch swL8 = Stopwatch.StartNew();
            UiAuditAccumulator accL8 = new();

            await RunOnUiThreadWithTimeoutAsync(() => {
                CloseNowPlayingSheet();
                if (nowPlayingSheetHost != null) {
                    nowPlayingSheetHost.Animate()?.Cancel();
                    OneUiMotionBlur.ClearMotionBlur(nowPlayingSheetHost);
                    OneUiMotionBlur.ClearHardwareLayer(nowPlayingSheetHost);
                    nowPlayingSheetHost.Visibility = ViewStates.Gone;
                    nowPlayingSheetHost.TranslationY = 0f;
                    nowPlayingSheetHost.Alpha = 1f;
                }
                return true;
            }, 4000).ConfigureAwait(false);

            string[] tabLabels = ["Tab0:Library", "Tab1:Sound", "Tab2:Export"];
            for (int t = 0; t < tabLabels.Length; t++) {
                ct.ThrowIfCancellationRequested();
                int tabIdx = t;
                (UiPerfSample perf, bool vsyncOk) = await MeasureUiTransitionAsync(tabLabels[tabIdx], () => {
                    SwitchTab(tabIdx);
                    if (tabIdx == 1) {
                        ClearSettingsSearch();
                        OpenSettingsCategory(-1);
                    }
                }, ct).ConfigureAwait(false);
                if (vsyncOk) vsyncSampledCount++;
                perfSamples.Add(perf);

                await RunOnUiThreadWithTimeoutAsync(() => {
                    SettleActiveUiAnimationsForAudit(tabIdx);
                    EnsureTabLaidOut(tabIdx);
                    AuditViewTree(rootFrameHost ?? Window?.DecorView, tabLabels[tabIdx], accL8, checkZeroSize: true);
                    return true;
                }, 4000).ConfigureAwait(false);
            }

            // Walk all 4 Sound categories in Pro mode so every hidden/pro card is audited too
            await RunOnUiThreadWithTimeoutAsync(() => {
                SwitchTab(1);
                SetSettingsProMode(true);
                ClearSettingsSearch();
                return true;
            }, 4000).ConfigureAwait(false);

            for (int cat = 0; cat < 4; cat++) {
                ct.ThrowIfCancellationRequested();
                int catIdx = cat;
                string catLabel = $"SettingsCat{catIdx}";
                (UiPerfSample perf, bool vsyncOk) = await MeasureUiTransitionAsync(catLabel, () => {
                    OpenSettingsCategory(catIdx);
                }, ct).ConfigureAwait(false);
                if (vsyncOk) vsyncSampledCount++;
                perfSamples.Add(perf);

                await RunOnUiThreadWithTimeoutAsync(() => {
                    EnsureTabLaidOut(1);
                    AuditViewTree((View)settingsCardsContainer ?? rootFrameHost, catLabel, accL8, checkZeroSize: true);
                    return true;
                }, 4000).ConfigureAwait(false);
            }

            string[] nowPlayingSubLabels = ["NowPlaying:Cover", "NowPlaying:Lyrics", "NowPlaying:3DStage"];
            for (int sub = 0; sub < nowPlayingSubLabels.Length; sub++) {
                ct.ThrowIfCancellationRequested();
                int subIdx = sub;
                (UiPerfSample perf, bool vsyncOk) = await MeasureUiTransitionAsync(nowPlayingSubLabels[subIdx], () => {
                    OpenNowPlayingSheet(subIdx);
                }, ct).ConfigureAwait(false);
                if (vsyncOk) vsyncSampledCount++;
                perfSamples.Add(perf);

                await RunOnUiThreadWithTimeoutAsync(() => {
                    if (nowPlayingSheetHost != null) {
                        SettleViewSubtreeAnimations(nowPlayingSheetHost, 0);
                        nowPlayingSheetHost.TranslationY = 0f;
                        EnsureRootLaidOut();
                        if (nowPlayingSheetHost.Width <= 0 || nowPlayingSheetHost.Height <= 0 || nowPlayingSheetHost.IsLayoutRequested) {
                            int w = rootFrameHost?.Width ?? (Resources?.DisplayMetrics?.WidthPixels ?? 1080);
                            int h = rootFrameHost?.Height ?? (Resources?.DisplayMetrics?.HeightPixels ?? 2400);
                            if (w > 0 && h > 0) {
                                nowPlayingSheetHost.Measure(
                                    View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly),
                                    View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly));
                                nowPlayingSheetHost.Layout(0, 0, w, h);
                            }
                        }
                        AuditViewTree(nowPlayingSheetHost, nowPlayingSubLabels[subIdx], accL8, checkZeroSize: true);
                    }
                    return true;
                }, 4000).ConfigureAwait(false);
            }

            await RunOnUiThreadWithTimeoutAsync(() => {
                CloseNowPlayingSheet();
                if (nowPlayingSheetHost != null) {
                    nowPlayingSheetHost.Animate()?.Cancel();
                    OneUiMotionBlur.ClearMotionBlur(nowPlayingSheetHost);
                    OneUiMotionBlur.ClearHardwareLayer(nowPlayingSheetHost);
                    nowPlayingSheetHost.Visibility = ViewStates.Gone;
                    nowPlayingSheetHost.TranslationY = 0f;
                    nowPlayingSheetHost.Alpha = 1f;
                }
                return true;
            }, 4000).ConfigureAwait(false);
            swL8.Stop();

            SelfCheckStatus stL8 = accL8.Failures.Count > 0
                ? SelfCheckStatus.Fail
                : (accL8.Warnings.Count > 0 ? SelfCheckStatus.Warn : SelfCheckStatus.Pass);
            string detL8 = accL8.Failures.Count > 0
                ? $"{accL8.Failures.Count} UI defect(s), {accL8.Warnings.Count} warn(s) across {accL8.ViewsInspected} views: {string.Join("; ", accL8.Failures.Take(8))}"
                : (accL8.Warnings.Count > 0
                    ? $"0 fatal UI defects, {accL8.Warnings.Count} layout/click warn(s) across {accL8.ViewsInspected} views: {string.Join("; ", accL8.Warnings.Take(8))}"
                    : $"All 3 tabs + 4 Sound categories clean ({accL8.ViewsInspected} views, {accL8.ButtonsInspected} buttons, {accL8.SelectorsInspected} selectors, 0 NaN/leaks/clipping)");
            results.Add(new SelfCheckItem {
                Id = "ui.tabs_and_settings",
                Layer = "L8-LiveUI",
                Name = "3 Tabs & 4 Sound Categories Audit",
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
                ("TechnicalStream", ShowTechnicalStreamDialog),
                ("PresetsAndPortability", ShowPresetsAndPortabilityModal),
                ("QuickEqCalibration", ShowQuickEqCalibrationDialog)
            ];

            int openedCount = 0;
            foreach ((string modalName, Action opener) in modals) {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke($"L9/L12 Modal: {modalName}");
                try {
                    (UiPerfSample perf, bool vsyncOk) = await MeasureUiTransitionAsync($"Modal:{modalName}", () => {
                        OneUiModalDialog.DismissAllActiveDialogs();
                        opener();
                    }, ct).ConfigureAwait(false);
                    if (vsyncOk) vsyncSampledCount++;
                    perfSamples.Add(perf);

                    await RunOnUiThreadWithTimeoutAsync(() => {
                        IReadOnlyList<Dialog> liveDlgs = OneUiModalDialog.GetActiveDialogs();
                        if (liveDlgs.Count > 0) {
                            openedCount++;
                            for (int i = 0; i < liveDlgs.Count; i++) {
                                View decor = liveDlgs[i]?.Window?.DecorView;
                                if (decor != null) {
                                    SettleViewSubtreeAnimations(decor, 0);
                                    AuditViewTree(decor, $"Modal:{modalName}", accL9, checkZeroSize: decor.Width > 0 && decor.Height > 0);
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
                SwitchTab(1);
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
            string modeTag = vsyncSampledCount == 0
                ? " [background UI-block mode]"
                : (vsyncSampledCount < perfSamples.Count ? $" [{vsyncSampledCount}/{perfSamples.Count} vsync]" : "");
            if (stalls.Count > 0) {
                string topStalls = string.Join(", ", stalls.Take(5).Select(s => $"{s.Target}(ui={s.UiThreadBlockMs}ms,1st={s.FirstFrameMs}ms,budget={s.BudgetMs}ms,drops={s.DroppedFrames})"));
                detL12 = $"{stalls.Count} UI stall(s), {slows.Count} slow ({perfSamples.Count} targets, P50={p50}ms, P95={p95}ms, drops={totalDrops}){modeTag}: {topStalls}";
            } else if (slows.Count > 0) {
                string topSlows = string.Join(", ", slows.Take(5).Select(s => $"{s.Target}(ui={s.UiThreadBlockMs}ms,1st={s.FirstFrameMs}ms,budget={s.BudgetMs}ms,drops={s.DroppedFrames})"));
                detL12 = $"0 stalls, {slows.Count} slower-than-budget target(s) ({perfSamples.Count} profiled, P50={p50}ms, P95={p95}ms, drops={totalDrops}){modeTag}: {topSlows}";
            } else {
                string worstDesc = worst != null ? $"{worst.Target}={worst.FirstFrameMs}ms" : "n/a";
                detL12 = string.Format(
                    CultureInfo.InvariantCulture,
                    "All {0} screens/modals within latency budget (P50={1}ms, P95={2}ms, max={3}, drops={4}){5}",
                    perfSamples.Count, p50, p95, worstDesc, totalDrops, modeTag);
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
            isRunningUiSelfAudit = false;
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
                    if (savedNowPlaying) {
                        OpenNowPlayingSheet(savedNowPlayingSub);
                    } else {
                        CloseNowPlayingSheet();
                    }
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
        int minButtonPx = Dp(24);
        int overflowTolPx = Math.Max(2, Dp(2));
        int overlapTolPx = Math.Max(3, Dp(4));
        int alignTolPx = Math.Max(4, Dp(5));
        int hardClipTolPx = Math.Max(4, Dp(4));
        int rootW = root.Width > 0 ? root.Width : (Resources?.DisplayMetrics?.WidthPixels ?? 1080);
        int[] rootLoc = new int[2];
        root.GetLocationOnScreen(rootLoc);
        int rootLeft = rootLoc[0];
        int rootRight = rootLeft + rootW;
        int[] loc = new int[2];
        View activeTabContainer = (tabContainers != null && activeTab >= 0 && activeTab < tabContainers.Length)
            ? tabContainers[activeTab]
            : null;

        void Walk(View v, int depth, bool inHScroll, bool inVScroll, int parentLeft, int parentRight, float cumScaleX) {
            if (v == null || depth > 24 || v.Visibility != ViewStates.Visible) {
                return;
            }
            if (activeTabContainer != null && tabContainers != null && v != activeTabContainer) {
                for (int ti = 0; ti < tabContainers.Length; ti++) {
                    if (ti != activeTab && ReferenceEquals(v, tabContainers[ti])) {
                        return;
                    }
                }
            }
            acc.ViewsInspected++;

            float nextScaleX = cumScaleX * (float.IsFinite(v.ScaleX) && v.ScaleX > 0.01f ? v.ScaleX : 1f);
            v.GetLocationOnScreen(loc);
            int vx = loc[0];
            int vy = loc[1];
            int vw = v.Width;
            int vh = v.Height;
            int scaledW = vw > 0 ? Math.Max(0, (int)Math.Round(vw * nextScaleX)) : 0;

            if (v is OneUiInlineSelector) {
                acc.SelectorsInspected++;
            }

            // Horizontal overflow outside HorizontalScrollView (catches clipped transport/action buttons)
            if (!inHScroll && vw > 0 && vh > 0 && rootW > 0) {
                int vRight = vx + scaledW;
                if (vRight > rootRight + overflowTolPx || vx < rootLeft - overflowTolPx) {
                    string label = v is TextView tvOv ? TruncateAuditText(tvOv.Text) : v.GetType().Name;
                    acc.Failures.Add($"{scopeName}: horizontal screen overflow on {v.GetType().Name} '{label}' (x={vx}, right={vRight}, rootRight={rootRight})");
                } else if (parentRight > parentLeft && (vRight > parentRight + overflowTolPx || vx < parentLeft - overflowTolPx)) {
                    if (v.Parent is ViewGroup pVg && pVg.ClipChildren) {
                        string label = v is TextView tvOv ? TruncateAuditText(tvOv.Text) : v.GetType().Name;
                        acc.Warnings.Add($"{scopeName}: parent horizontal clip on {v.GetType().Name} '{label}' (right={vRight} > parentRight={parentRight})");
                    }
                }
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
                    // Check zero-size non-empty TextView on laid-out screens
                    if (checkZeroSize && (vw <= 0 || vh <= 0)) {
                        acc.Warnings.Add($"{scopeName}: zero-size visible TextView '{TruncateAuditText(txt)}'");
                    }
                    // Check hard-clipped single-line TextView without ellipsize
                    if (vw > 0 && tv.MaxLines == 1 && tv.Ellipsize == null && v is not EditText && tv.Paint != null) {
                        float meas = tv.Paint.MeasureText(txt) + tv.CompoundPaddingLeft + tv.CompoundPaddingRight;
                        if (float.IsFinite(meas) && meas > vw + hardClipTolPx) {
                            acc.Warnings.Add($"{scopeName}: hard-clipped text in {v.GetType().Name} '{TruncateAuditText(txt)}' (meas={(int)Math.Ceiling(meas)}px > w={vw}px)");
                        }
                    }
                    // Check ellipsized Button text (action buttons must not truncate labels)
                    if (v is Button && v is not CompoundButton && tv.Layout != null) {
                        int ellSum = 0;
                        int lc = tv.Layout.LineCount;
                        for (int li = 0; li < lc; li++) {
                            ellSum += tv.Layout.GetEllipsisCount(li);
                        }
                        if (ellSum > 0) {
                            acc.Warnings.Add($"{scopeName}: ellipsized Button '{TruncateAuditText(txt)}' (ell={ellSum})");
                        }
                    }
                }
            }

            if (v is Button btn && btn is not CompoundButton) {
                acc.ButtonsInspected++;
                if (vh > 0 && (vh < minButtonPx || vw < minButtonPx)) {
                    acc.Failures.Add($"{scopeName}: crushed button touch target '{TruncateAuditText(btn.Text)}' ({vw}x{vh}px < {minButtonPx}px)");
                }
                if (checkZeroSize && (vw <= 0 || vh <= 0)) {
                    acc.Warnings.Add($"{scopeName}: zero-size visible button '{TruncateAuditText(btn.Text)}'");
                }
                if (btn.Clickable && btn.Enabled && !btn.HasOnClickListeners) {
                    acc.Warnings.Add($"{scopeName}: button without click listener '{TruncateAuditText(btn.Text)}'");
                }
            }

            // Detect horizontal LinearLayouts with >=2 visible Buttons missing BaselineAligned = false (BL-013),
            // sibling overlap, and vertical center-Y alignment drift
            if (v is LinearLayout ll) {
                bool isHoriz = ll.Orientation == Orientation.Horizontal;
                if (isHoriz && ll.BaselineAligned) {
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
                int prevRight = int.MinValue;
                int prevBottom = int.MinValue;
                int firstBtnCenterY = int.MinValue;
                for (int i = 0; i < ll.ChildCount; i++) {
                    View ch = ll.GetChildAt(i);
                    if (ch == null || ch.Visibility != ViewStates.Visible || ch.Width <= 0 || ch.Height <= 0) {
                        continue;
                    }
                    // Use unscaled layout coordinates (Left/Right/Top/Bottom + translation) so ancestor modal
                    // scale-in animations never produce phantom sibling overlaps while real negative-margin overlaps are caught.
                    int chLeft = ch.Left + (int)Math.Round(ch.TranslationX);
                    int chRight = ch.Right + (int)Math.Round(ch.TranslationX);
                    int chTop = ch.Top + (int)Math.Round(ch.TranslationY);
                    int chBottom = ch.Bottom + (int)Math.Round(ch.TranslationY);
                    if (isHoriz) {
                        if (prevRight != int.MinValue && chLeft < prevRight - overlapTolPx) {
                            acc.Failures.Add($"{scopeName}: overlapping horizontal siblings in LinearLayout (overlap={prevRight - chLeft}px)");
                        }
                        prevRight = Math.Max(prevRight, chRight);
                        if (ch is Button && ch is not CompoundButton) {
                            int cy = (chTop + chBottom) / 2;
                            if (firstBtnCenterY == int.MinValue) {
                                firstBtnCenterY = cy;
                            } else if (Math.Abs(cy - firstBtnCenterY) > alignTolPx) {
                                acc.Warnings.Add($"{scopeName}: misaligned horizontal button row (dy={Math.Abs(cy - firstBtnCenterY)}px)");
                            }
                        }
                    } else {
                        if (prevBottom != int.MinValue && chTop < prevBottom - overlapTolPx) {
                            acc.Failures.Add($"{scopeName}: overlapping vertical siblings in LinearLayout (overlap={prevBottom - chTop}px)");
                        }
                        prevBottom = Math.Max(prevBottom, chBottom);
                    }
                }
            }

            if (v is ViewGroup vg) {
                bool nextHScroll = inHScroll || v is HorizontalScrollView;
                bool nextVScroll = inVScroll || v is ScrollView || v is AbsListView;
                int nextPLeft = vw > 0 ? vx : parentLeft;
                int nextPRight = scaledW > 0 ? vx + scaledW : parentRight;
                for (int i = 0; i < vg.ChildCount; i++) {
                    Walk(vg.GetChildAt(i), depth + 1, nextHScroll, nextVScroll, nextPLeft, nextPRight, nextScaleX);
                }
            }
        }

        Walk(root, 0, inHScroll: false, inVScroll: false, parentLeft: rootLeft, parentRight: rootRight, cumScaleX: 1f);
    }

    static string TruncateAuditText(string s) {
        if (string.IsNullOrEmpty(s)) {
            return "";
        }
        string oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 38 ? oneLine : oneLine[..38] + "...";
    }
}
