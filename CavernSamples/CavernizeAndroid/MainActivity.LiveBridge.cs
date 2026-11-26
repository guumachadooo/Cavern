using System.Text.Json;

using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;
using CavernizeAndroid.Views;

using File = System.IO.File;
using MemoryStream = System.IO.MemoryStream;

namespace CavernizeAndroid;

/// <summary>
/// Live Bridge v3 host integration on <see cref="MainActivity"/>: safely marshals UI inspection
/// (including active <see cref="OneUiModalDialog"/> windows), universal UI automation, playback
/// control, tab navigation, live reflection roots, downscaled screenshots, and L8-L10 live UI
/// self-audits onto the Android UI thread with bounded timeouts.
/// </summary>
public partial class MainActivity : ILiveBridgeAppHost {
    Task<T> RunOnUiThreadWithTimeoutAsync<T>(Func<T> action, int timeoutMs = 6000) {
        if (uiDestroyed || IsFinishing) {
            return Task.FromException<T>(new InvalidOperationException("MainActivity is destroyed or finishing."));
        }
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() => {
            try {
                tcs.TrySetResult(action());
            } catch (Exception ex) {
                tcs.TrySetException(ex);
            }
        });
        return WaitWithTimeoutAsync(tcs.Task, timeoutMs);
    }

    static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, int timeoutMs) {
        Task delay = Task.Delay(timeoutMs);
        Task completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed != task) {
            throw new TimeoutException($"UI host operation timed out after {timeoutMs}ms.");
        }
        return await task.ConfigureAwait(false);
    }

    List<View> GetInspectableUiRoots(bool includeDialogs = true) {
        List<View> roots = [];
        if (includeDialogs) {
            IReadOnlyList<Dialog> dlgs = OneUiModalDialog.GetActiveDialogs();
            for (int i = dlgs.Count - 1; i >= 0; i--) {
                View dv = dlgs[i]?.Window?.DecorView;
                if (dv != null) {
                    roots.Add(dv);
                }
            }
        }
        View mainRoot = rootFrameHost ?? Window?.DecorView;
        if (mainRoot != null) {
            roots.Add(mainRoot);
        }
        return roots;
    }

    Task<string> ILiveBridgeAppHost.GetAppStateJsonAsync() => RunOnUiThreadWithTimeoutAsync(() => {
        return JsonSerializer.Serialize(new {
            uiHostAttached = true,
            activeTab,
            activeSettingsCategory,
            isSettingsProMode,
            activeDialogCount = OneUiModalDialog.GetActiveDialogs().Count,
            isOperationRunning,
            isPreparingTrueHD,
            loadedFile = LoadedFile?.Path ?? "",
            selectedTrack = selectedTrack?.ToString() ?? "",
            selectedCodec = selectedTrack?.Codec.ToString() ?? "",
            selectedSupported = selectedTrack?.Supported ?? false,
            isPlaying = playerEngine?.IsPlaying ?? false,
            currentSeconds = Math.Round(playerEngine?.CurrentSeconds ?? 0, 2),
            totalSeconds = Math.Round(playerEngine?.TotalSeconds ?? 0, 2),
            loop = playerEngine?.Loop ?? false,
            currentTitle = currentMediaMeta?.Title ?? "",
            currentArtist = currentMediaMeta?.Artist ?? "",
            headerStatus = currentHeaderStatusText ?? "",
            trackSummary = currentTrackSummaryText ?? "",
            libraryTrackCount = (librarySnapshot ?? MediaLibraryService.CachedSnapshot)?.Tracks?.Count ?? 0,
            standbyWakeRunning = LiveBridgeWakeService.IsRunning
        }, new JsonSerializerOptions { WriteIndented = true });
    });

    Task<string> ILiveBridgeAppHost.GetRuntimeTelemetryJsonAsync() =>
        Task.FromResult(AndroidRuntimeTelemetry.BuildRuntimeTelemetryJson(this, audioOutput));

    Task<string> ILiveBridgeAppHost.GetDspTelemetryJsonAsync() => RunOnUiThreadWithTimeoutAsync(() => {
        var rot = playerEngine?.ListenerRotation ?? default;
        var pos = playerEngine?.ListenerPosition ?? default;
        return JsonSerializer.Serialize(new {
            headphoneVirtualizer = playerEngine?.HeadphoneVirtualizer ?? false,
            environmentType = playerEngine?.EnvironmentType.ToString() ?? "",
            audioQuality = playerEngine?.AudioQuality.ToString() ?? "",
            distanceSimulation = playerEngine?.DistanceSimulation ?? false,
            hybrid714BuildCount = playerEngine?.Hybrid714BuildCount ?? 0,
            listenerRotation = new { pitch = Math.Round(rot.X, 2), yaw = Math.Round(rot.Y, 2), roll = Math.Round(rot.Z, 2) },
            listenerPosition = new { x = Math.Round(pos.X, 2), y = Math.Round(pos.Y, 2), z = Math.Round(pos.Z, 2) },
            iso226Enabled = store?.Iso226Enabled ?? false,
            lgsrEnabled = store?.LgsrEnabled ?? false,
            loudnessNormMode = store?.LoudnessNormModeIndex ?? 0,
            headTrackingEnabled = store?.HeadTrackingEnabled ?? false,
            headTrackingAvailable = headTrackingManager != null,
            proAudioBridgeAvailable = CavernizeAndroid.Audio.NativeProAudioBridge.IsAvailable,
            audioXRuns = AndroidRuntimeTelemetry.BuildAudioXRunObject(audioOutput),
            frameMetrics = AndroidRuntimeTelemetry.BuildFrameMetricsObject(),
            thermalStatus = AndroidRuntimeTelemetry.GetThermalStatusCode(this)
        }, new JsonSerializerOptions { WriteIndented = true });
    });

    Task<string> ILiveBridgeAppHost.QueryLibraryJsonAsync(string query, int limit, bool musicOnly) => RunOnUiThreadWithTimeoutAsync(() => {
        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot ?? MediaLibraryService.TryLoadCachedSnapshot(this);
        string normQ = NormalizeSearchFold(query);
        bool hasQ = !string.IsNullOrEmpty(normQ);
        IEnumerable<LibraryTrackItem> source = snap?.Tracks ?? [];
        if (musicOnly) {
            source = source.Where(t => !t.HasNoAudioTrack && !t.IsVideo && !t.IsLikelyNonMusic);
        }
        IEnumerable<LibraryTrackItem> filtered = source.Where(t =>
            !hasQ ||
            MatchesSearchQuery(t.Title, normQ) ||
            MatchesSearchQuery(t.Artist, normQ) ||
            MatchesSearchQuery(t.AlbumArtist, normQ) ||
            MatchesSearchQuery(t.Album, normQ) ||
            MatchesSearchQuery(t.FilePath, normQ));
        if (!hasQ) {
            filtered = filtered
                .OrderByDescending(t => !string.IsNullOrWhiteSpace(t.Album) && !t.IsVideo && !t.IsLikelyNonMusic)
                .ThenByDescending(t => t.DurationMs > 0);
        }
        var tracks = filtered
            .Take(Math.Clamp(limit, 1, 100))
            .Select(t => new {
                t.Title,
                t.Artist,
                t.Album,
                t.DurationMs,
                t.FormatBadge,
                t.IsSpatial,
                t.ChannelCount,
                t.FilePath
            })
            .ToList();
        return JsonSerializer.Serialize(new {
            totalTracks = snap?.Tracks?.Count ?? 0,
            returned = tracks.Count,
            musicOnly,
            query = query ?? "",
            tracks
        }, new JsonSerializerOptions { WriteIndented = true });
    });

    void EnsureRootLaidOut() {
        View root = rootFrameHost ?? Window?.DecorView;
        if (root == null) {
            return;
        }
        int w = root.Width;
        int h = root.Height;
        if (w <= 0 || h <= 0) {
            return;
        }
        bool needsLayout = root.IsLayoutRequested ||
            (rootLayoutHost != null && rootLayoutHost.IsLayoutRequested) ||
            (workspaceChromeHost != null && workspaceChromeHost.Visibility == ViewStates.Visible && workspaceChromeHost.Height <= 0);
        if (!needsLayout) {
            return;
        }
        int wSpec = View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly);
        int hSpec = View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly);
        root.Measure(wSpec, hSpec);
        root.Layout(root.Left, root.Top, root.Left + w, root.Top + h);
    }

    void EnsureTabLaidOut(int tabIdx) {
        EnsureRootLaidOut();
        if (tabContainers == null || tabIdx < 0 || tabIdx >= tabContainers.Length) {
            return;
        }
        View tab = tabContainers[tabIdx];
        if (tab == null || tab.Visibility != ViewStates.Visible) {
            return;
        }
        if (tab.Width > 0 && tab.Height > 0 && !tab.IsLayoutRequested) {
            return;
        }
        View host = (tab.Parent as View) ?? rootFrameHost ?? Window?.DecorView;
        int w = host?.Width ?? 0;
        int h = host?.Height ?? 0;
        if (w <= 0 || h <= 0) {
            return;
        }
        int wSpec = View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly);
        int hSpec = View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly);
        tab.Measure(wSpec, hSpec);
        tab.Layout(0, 0, w, h);
    }

    Task<string> ILiveBridgeAppHost.DumpUiTreeJsonAsync(int maxDepth) => RunOnUiThreadWithTimeoutAsync(() => {
        EnsureTabLaidOut(activeTab);
        int nodes = 0;
        View activeTabContainer = (tabContainers != null && activeTab >= 0 && activeTab < tabContainers.Length)
            ? tabContainers[activeTab]
            : null;
        object Walk(View v, int depth) {
            if (v == null || nodes >= 320) {
                return null;
            }
            if (activeTabContainer != null && tabContainers != null && v != activeTabContainer) {
                for (int ti = 0; ti < tabContainers.Length; ti++) {
                    if (ti != activeTab && ReferenceEquals(v, tabContainers[ti])) {
                        return null;
                    }
                }
            }
            nodes++;
            string txt = v is TextView tv ? (tv.Text ?? "") : "";
            if (txt.Length > 100) {
                txt = txt[..100] + "...";
            }
            string cd = v.ContentDescription ?? "";
            bool? isChecked = v is CompoundButton cb ? cb.Checked : null;
            int? progress = v is SeekBar sb ? sb.Progress : (v is OneUiWaveMediaSeekBar osb ? osb.Progress : null);
            int? selectedIdx = v is OneUiInlineSelector sel ? sel.SelectedIndex : null;
            List<object> children = null;
            if (depth < maxDepth && v is ViewGroup vg && vg.ChildCount > 0) {
                children = [];
                for (int i = 0; i < vg.ChildCount && nodes < 320; i++) {
                    View ch = vg.GetChildAt(i);
                    if (ch != null && ch.Visibility == ViewStates.Visible) {
                        bool isInactiveTab = false;
                        if (tabContainers != null) {
                            for (int t = 0; t < tabContainers.Length; t++) {
                                if (ReferenceEquals(ch, tabContainers[t]) && t != activeTab) {
                                    isInactiveTab = true;
                                    break;
                                }
                            }
                        }
                        if (isInactiveTab) {
                            continue;
                        }
                        object cNode = Walk(ch, depth + 1);
                        if (cNode != null) {
                            children.Add(cNode);
                        }
                    }
                }
            }
            return new {
                type = v.GetType().Name,
                clickable = v.Clickable && v.Enabled,
                enabled = v.Enabled,
                text = string.IsNullOrEmpty(txt) ? null : txt,
                desc = string.IsNullOrEmpty(cd) ? null : cd,
                @checked = isChecked,
                progress,
                selectedIdx,
                w = v.Width,
                h = v.Height,
                children = children != null && children.Count > 0 ? children : null
            };
        }

        IReadOnlyList<Dialog> activeDlgs = OneUiModalDialog.GetActiveDialogs();
        List<object> dialogTrees = [];
        for (int i = 0; i < activeDlgs.Count; i++) {
            View dv = activeDlgs[i]?.Window?.DecorView;
            if (dv != null) {
                dialogTrees.Add(Walk(dv, 0));
            }
        }
        View mainRoot = rootFrameHost ?? Window?.DecorView;
        return JsonSerializer.Serialize(new {
            activeDialogs = dialogTrees.Count,
            dialogs = dialogTrees.Count > 0 ? dialogTrees : null,
            activity = mainRoot != null ? Walk(mainRoot, 0) : null
        }, new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    });

    Task<string> ILiveBridgeAppHost.CaptureUiScreenshotJsonAsync(int maxWidth, int quality) => RunOnUiThreadWithTimeoutAsync(() => {
        EnsureTabLaidOut(activeTab);
        IReadOnlyList<Dialog> dlgs = OneUiModalDialog.GetActiveDialogs();
        View root = dlgs.Count > 0 ? (dlgs[^1]?.Window?.DecorView?.RootView ?? Window?.DecorView?.RootView) : (Window?.DecorView?.RootView ?? rootFrameHost);
        if (root == null || root.Width <= 0 || root.Height <= 0) {
            return JsonSerializer.Serialize(new { ok = false, error = "Window DecorView not laid out yet." });
        }
        int srcW = root.Width;
        int srcH = root.Height;
        float scale = srcW > maxWidth ? (float)maxWidth / srcW : 1f;
        int dstW = Math.Max(64, (int)Math.Round(srcW * scale));
        int dstH = Math.Max(64, (int)Math.Round(srcH * scale));

        using Bitmap bmp = Bitmap.CreateBitmap(dstW, dstH, Bitmap.Config.Argb8888);
        using Canvas canvas = new(bmp);
        canvas.Scale(scale, scale);
        root.Draw(canvas);

        using MemoryStream ms = new();
        bmp.Compress(Bitmap.CompressFormat.Jpeg, Math.Clamp(quality, 40, 90), ms);
        string b64 = Convert.ToBase64String(ms.ToArray());
        return JsonSerializer.Serialize(new {
            width = dstW,
            height = dstH,
            quality,
            bytes = ms.Length,
            mime = "image/jpeg",
            capturedDialog = dlgs.Count > 0,
            imageBase64 = b64
        });
    });

    Task<(bool ok, string message)> ILiveBridgeAppHost.SwitchTabAsync(int tabIndex, string tabName) => RunOnUiThreadWithTimeoutAsync(() => {
        int target = tabIndex;
        if (target < 0 && !string.IsNullOrWhiteSpace(tabName)) {
            target = tabName.Trim().ToLowerInvariant() switch {
                "player" or "music" or "play" => 0,
                "room" or "sala" or "3d" => 1,
                "studio" or "estudio" or "convert" => 2,
                "stems" or "tracks" => 3,
                "settings" or "config" => 4,
                _ => -1
            };
        }
        if (target < 0 || target > 4) {
            return (false, "Invalid tab index/name (valid: 0..4 or player/room/studio/stems/settings).");
        }
        SwitchTab(target);
        EnsureTabLaidOut(target);
        return (true, $"Switched to tab {target}.");
    });

    Task<(bool ok, string message)> ILiveBridgeAppHost.PlaybackControlAsync(string action, double? value) => RunOnUiThreadWithTimeoutAsync(() => {
        string act = (action ?? "").Trim().ToLowerInvariant();
        switch (act) {
            case "play":
                if (!(playerEngine?.IsPlaying ?? false)) {
                    TogglePlayPause();
                }
                return (true, $"Playback play requested (isPlaying={playerEngine?.IsPlaying ?? false}).");
            case "pause":
                if (playerEngine?.IsPlaying ?? false) {
                    TogglePlayPause();
                }
                return (true, $"Playback pause requested (isPlaying={playerEngine?.IsPlaying ?? false}).");
            case "toggle":
                TogglePlayPause();
                return (true, $"Playback toggled (isPlaying={playerEngine?.IsPlaying ?? false}).");
            case "stop":
                StopPlayback();
                return (true, "Playback stopped.");
            case "next":
                PlayLibraryQueueStep(+1);
                return (true, "Skipped to next track in queue.");
            case "prev":
            case "previous":
                PlayLibraryQueueStep(-1);
                return (true, "Skipped to previous track in queue.");
            case "seek":
                if (!value.HasValue) {
                    return (false, "seek requires {value: seconds}.");
                }
                SeekAbsoluteSeconds(value.Value);
                return (true, $"Seeked to {value.Value:0.0}s.");
            case "seek_rel":
                SeekRelative(value ?? 10.0);
                return (true, $"Seeked relative {(value ?? 10.0):+0.0;-0.0}s.");
            case "loop":
                ToggleLoopMode();
                return (true, $"Loop mode now: {playerEngine?.Loop ?? false}.");
            default:
                return (false, $"Unknown playback action '{act}'.");
        }
    });

    Task<(bool ok, string message)> ILiveBridgeAppHost.OpenFileAsync(string path, bool autoPlay, int? switchToTab) => RunOnUiThreadWithTimeoutAsync(() => {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
            return (false, $"File not found: '{path}'");
        }
        OpenLocalPathWithFallbackAsync(path, switchToTab: switchToTab, autoPlay: autoPlay, bypassBusyGuard: true);
        return (true, $"Opening '{System.IO.Path.GetFileName(path)}' (autoPlay={autoPlay}).");
    });

    Task<(bool ok, string message)> ILiveBridgeAppHost.ExecuteUiActionAsync(string action, string message) => RunOnUiThreadWithTimeoutAsync(() => {
        switch ((action ?? "").Trim().ToLowerInvariant()) {
            case "refresh_ui":
                RefreshAllUiState();
                RefreshLogSummaryLabel();
                return (true, "Refreshed all UI state.");
            case "rescan_library":
                RefreshLibraryScanAsync(force: true);
                return (true, "Triggered forced local media library rescan.");
            case "check_updates":
                _ = CheckForUpdatesAsync(silent: false);
                return (true, "Triggered GitHub Releases update check.");
            case "reset_head_pose":
                headTrackingManager?.Recenter();
                return (true, "Recentered head-tracking forward reference.");
            case "dismiss_dialogs":
                int dismissed = OneUiModalDialog.DismissAllActiveDialogs();
                return (true, $"Dismissed {dismissed} active modal dialog(s).");
            case "toast":
                Toast.MakeText(this, string.IsNullOrWhiteSpace(message) ? "Cavernize Live Bridge" : message, ToastLength.Short).Show();
                return (true, "Displayed toast on screen.");
            default:
                return (false, $"Unknown ui_action '{action}'.");
        }
    });

    Task<(bool ok, string message)> ILiveBridgeAppHost.ClickUiElementAsync(string textQuery, int index) => RunOnUiThreadWithTimeoutAsync(() => {
        if (string.IsNullOrWhiteSpace(textQuery)) {
            return (false, "ui_click requires {text: string}.");
        }
        List<View> matches = FindViews(textQuery, typeFilter: "", clickableOnly: true, includeDialogs: true);
        if (matches.Count == 0) {
            return (false, $"No visible clickable View found matching '{textQuery}'.");
        }
        int pick = Math.Clamp(index, 0, matches.Count - 1);
        bool clicked = matches[pick].PerformClick();
        return (clicked, clicked ? $"Clicked match #{pick} of {matches.Count} for '{textQuery}'." : $"PerformClick returned false for '{textQuery}'.");
    });

    Task<(bool ok, string json)> ILiveBridgeAppHost.UiInteractAsync(JsonElement argsEl) => RunOnUiThreadWithTimeoutAsync(() => {
        string action = ReadJsonStr(argsEl, "action", "find").Trim().ToLowerInvariant();
        string query = ReadJsonStr(argsEl, "query", ReadJsonStr(argsEl, "text", ""));
        string typeFilter = ReadJsonStr(argsEl, "type", "");
        int index = ReadJsonInt(argsEl, "index", 0, 0, 200);
        bool includeDialogs = ReadJsonBool(argsEl, "includeDialogs", true);

        switch (action) {
            case "dismiss_dialogs": {
                int count = OneUiModalDialog.DismissAllActiveDialogs();
                return (true, JsonSerializer.Serialize(new { action, dismissed = count }));
            }
            case "dump_dialogs": {
                IReadOnlyList<Dialog> dlgs = OneUiModalDialog.GetActiveDialogs();
                return (true, JsonSerializer.Serialize(new {
                    activeDialogs = dlgs.Count,
                    showing = dlgs.Select((d, i) => new { index = i, isShowing = d.IsShowing, hasWindow = d.Window != null }).ToList()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            case "back": {
                IReadOnlyList<Dialog> dlgs = OneUiModalDialog.GetActiveDialogs();
                if (dlgs.Count > 0) {
                    dlgs[^1].Dismiss();
                    return (true, JsonSerializer.Serialize(new { action, result = "dismissed top dialog" }));
                }
                if (activeTab == 4 && activeSettingsCategory >= 0) {
                    OpenSettingsCategory(-1);
                    return (true, JsonSerializer.Serialize(new { action, result = "returned to Settings Hub" }));
                }
                if (HandleInternalBackStep()) {
                    return (true, JsonSerializer.Serialize(new { action, result = "handled internal back step" }));
                }
                MoveTaskToBack(true);
                return (true, JsonSerializer.Serialize(new { action, result = "moved task to back" }));
            }
            case "find": {
                List<View> found = FindViews(query, typeFilter, clickableOnly: false, includeDialogs);
                var summary = found.Take(80).Select((v, i) => {
                    int[] loc = new int[2];
                    v.GetLocationOnScreen(loc);
                    return new {
                        index = i,
                        type = v.GetType().Name,
                        text = v is TextView tv ? (tv.Text ?? "") : "",
                        desc = v.ContentDescription ?? "",
                        clickable = v.Clickable && v.Enabled,
                        @checked = v is CompoundButton cb ? (bool?)cb.Checked : null,
                        progress = v is SeekBar sb ? (int?)sb.Progress : (v is OneUiWaveMediaSeekBar osb ? (int?)osb.Progress : null),
                        selectedIdx = v is OneUiInlineSelector sel ? (int?)sel.SelectedIndex : null,
                        x = loc[0],
                        y = loc[1],
                        w = v.Width,
                        h = v.Height
                    };
                }).ToList();
                return (true, JsonSerializer.Serialize(new { total = found.Count, returned = summary.Count, views = summary }, new JsonSerializerOptions { WriteIndented = true }));
            }
            case "click":
            case "long_click": {
                List<View> matches = FindViews(query, typeFilter, clickableOnly: true, includeDialogs);
                if (matches.Count == 0) {
                    matches = FindViews(query, typeFilter, clickableOnly: false, includeDialogs);
                }
                if (matches.Count == 0) {
                    return (false, $"No visible View found matching query='{query}' type='{typeFilter}'.");
                }
                View target = matches[Math.Clamp(index, 0, matches.Count - 1)];
                bool ok = action == "long_click" ? target.PerformLongClick() : target.PerformClick();
                return (ok, JsonSerializer.Serialize(new { action, ok, matched = matches.Count, type = target.GetType().Name }));
            }
            case "set_text": {
                string value = ReadJsonStr(argsEl, "value", "");
                List<View> matches = FindViews(query, string.IsNullOrEmpty(typeFilter) ? "EditText" : typeFilter, clickableOnly: false, includeDialogs);
                if (matches.Count == 0 || matches[Math.Clamp(index, 0, matches.Count - 1)] is not EditText et) {
                    return (false, $"No visible EditText found matching '{query}'.");
                }
                et.Text = value;
                return (true, JsonSerializer.Serialize(new { action, ok = true, length = value.Length }));
            }
            case "set_checked": {
                bool val = ReadJsonBool(argsEl, "value", true);
                List<View> matches = FindViews(query, typeFilter, clickableOnly: false, includeDialogs)
                    .Where(v => v is CompoundButton)
                    .ToList();
                if (matches.Count == 0) {
                    return (false, $"No visible Switch/CheckBox found matching '{query}'.");
                }
                CompoundButton cb = (CompoundButton)matches[Math.Clamp(index, 0, matches.Count - 1)];
                cb.Checked = val;
                return (true, JsonSerializer.Serialize(new { action, ok = true, @checked = cb.Checked }));
            }
            case "set_progress": {
                int val = ReadJsonInt(argsEl, "value", 0, 0, 100000);
                List<View> matches = FindViews(query, typeFilter, clickableOnly: false, includeDialogs)
                    .Where(v => v is SeekBar or OneUiWaveMediaSeekBar)
                    .ToList();
                if (matches.Count == 0) {
                    return (false, $"No visible SeekBar/OneUiWaveMediaSeekBar found matching '{query}'.");
                }
                View target = matches[Math.Clamp(index, 0, matches.Count - 1)];
                if (target is OneUiWaveMediaSeekBar osb) {
                    osb.Progress = val;
                } else if (target is SeekBar sb) {
                    sb.Progress = val;
                }
                return (true, JsonSerializer.Serialize(new { action, ok = true, value = val, type = target.GetType().Name }));
            }
            case "select_item": {
                int itemIndex = ReadJsonInt(argsEl, "value", 0, 0, 500);
                List<View> matches = FindViews(query, typeFilter, clickableOnly: false, includeDialogs)
                    .Where(v => v is OneUiInlineSelector)
                    .ToList();
                if (matches.Count == 0) {
                    return (false, $"No visible OneUiInlineSelector found matching '{query}'.");
                }
                OneUiInlineSelector sel = (OneUiInlineSelector)matches[Math.Clamp(index, 0, matches.Count - 1)];
                sel.SetSelection(itemIndex, notify: true);
                return (true, JsonSerializer.Serialize(new { action, ok = true, selectedIndex = sel.SelectedIndex, itemCount = sel.ItemCount }));
            }
            case "scroll_by": {
                int dy = ReadJsonInt(argsEl, "dy", 400, -10000, 10000);
                List<View> scrollers = FindViews(query, typeFilter, clickableOnly: false, includeDialogs)
                    .Where(v => v is ScrollView or HorizontalScrollView)
                    .ToList();
                if (scrollers.Count == 0) {
                    return (false, "No visible ScrollView found.");
                }
                View sv = scrollers[Math.Clamp(index, 0, scrollers.Count - 1)];
                sv.ScrollBy(0, dy);
                return (true, JsonSerializer.Serialize(new { action, ok = true, scrollY = sv.ScrollY }));
            }
            case "tap_xy": {
                float x = (float)ReadJsonDouble(argsEl, "x", 0);
                float y = (float)ReadJsonDouble(argsEl, "y", 0);
                View target = GetInspectableUiRoots(includeDialogs).FirstOrDefault();
                if (target == null) {
                    return (false, "No window available for tap_xy.");
                }
                long now = SystemClock.UptimeMillis();
                using MotionEvent down = MotionEvent.Obtain(now, now, MotionEventActions.Down, x, y, 0);
                using MotionEvent up = MotionEvent.Obtain(now, now + 45, MotionEventActions.Up, x, y, 0);
                bool d1 = target.DispatchTouchEvent(down);
                bool d2 = target.DispatchTouchEvent(up);
                return (d1 || d2, JsonSerializer.Serialize(new { action, x, y, dispatched = d1 || d2 }));
            }
            case "swipe_xy": {
                float x1 = (float)ReadJsonDouble(argsEl, "x1", 0);
                float y1 = (float)ReadJsonDouble(argsEl, "y1", 0);
                float x2 = (float)ReadJsonDouble(argsEl, "x2", x1);
                float y2 = (float)ReadJsonDouble(argsEl, "y2", y1);
                View target = GetInspectableUiRoots(includeDialogs).FirstOrDefault();
                if (target == null) {
                    return (false, "No window available for swipe_xy.");
                }
                long now = SystemClock.UptimeMillis();
                using MotionEvent down = MotionEvent.Obtain(now, now, MotionEventActions.Down, x1, y1, 0);
                target.DispatchTouchEvent(down);
                for (int s = 1; s <= 6; s++) {
                    float t = s / 6f;
                    using MotionEvent mv = MotionEvent.Obtain(now, now + s * 20, MotionEventActions.Move, x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, 0);
                    target.DispatchTouchEvent(mv);
                }
                using MotionEvent up = MotionEvent.Obtain(now, now + 150, MotionEventActions.Up, x2, y2, 0);
                bool end = target.DispatchTouchEvent(up);
                return (true, JsonSerializer.Serialize(new { action, x1, y1, x2, y2, end }));
            }
            default:
                return (false, $"Unknown ui_interact action '{action}'. Valid: find, click, long_click, set_text, set_checked, set_progress, select_item, scroll_by, tap_xy, swipe_xy, back, dismiss_dialogs, dump_dialogs.");
        }
    });

    Task<(bool ok, string output)> ILiveBridgeAppHost.ExecuteOnUiThreadReflectAsync(Func<(bool ok, string output)> fn) =>
        RunOnUiThreadWithTimeoutAsync(fn, 15_000);

    object ILiveBridgeAppHost.GetReflectionRoot(string rootName) {
        return (rootName ?? "").Trim().ToLowerInvariant() switch {
            "activity" or "main" or "this" => this,
            "store" or "settings" => store,
            "player" or "engine" => playerEngine,
            "audiodevice" or "device" => audioDeviceProfileManager,
            "headtracking" or "head" => headTrackingManager,
            "proaudio" or "usb" => usbProAudioManager,
            "dialogs" => OneUiModalDialog.GetActiveDialogs(),
            _ => null
        };
    }

    Task<(List<SelfCheckItem> Checks, List<UiPerfSample> PerfSamples)> ILiveBridgeAppHost.RunLiveUiSelfAuditAsync(Action<string> onProgress, CancellationToken ct) =>
        RunLiveUiSelfAuditInternalAsync(onProgress, ct);

    Task ILiveBridgeAppHost.ApplySettingsChangedAsync() => RunOnUiThreadWithTimeoutAsync(() => {
        if (store != null) {
            Cavernize.Logic.Spatial.ProAudioMicSource micSrc =
                (Cavernize.Logic.Spatial.ProAudioMicSource)Math.Clamp(store.ProAudioMicSource, 0, 2);
            if (audioDeviceProfileManager != null) {
                audioDeviceProfileManager.PreferredMicSource = micSrc;
            }
            usbProAudioManager?.UpdateMicSource(micSrc);
            if (audioOutput != null) {
                audioOutput.LowLatencyMode = store.LowLatencyAudio;
                audioOutput.UsbDacNativeSampleRate = store.UsbDacNativeSampleRate;
                audioOutput.ApplyProAudioConfig(store.BuildProAudioConfig());
            }
        }
        RefreshAllUiState();
        RefreshLogSummaryLabel();
        return true;
    });

    List<View> FindViews(string textQuery, string typeFilter, bool clickableOnly, bool includeDialogs) {
        List<View> matches = [];
        void Collect(View v) {
            if (v == null || v.Visibility != ViewStates.Visible) {
                return;
            }
            string txt = v is TextView tv ? (tv.Text ?? "") : "";
            string cd = v.ContentDescription ?? "";
            string typeName = v.GetType().Name;
            bool textOk = string.IsNullOrWhiteSpace(textQuery) ||
                          txt.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                          cd.Contains(textQuery, StringComparison.OrdinalIgnoreCase);
            bool typeOk = string.IsNullOrWhiteSpace(typeFilter) ||
                          typeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase);
            bool clickOk = !clickableOnly || (v.Clickable && v.Enabled);
            if (textOk && typeOk && clickOk) {
                matches.Add(v);
            }
            if (v is ViewGroup vg) {
                for (int i = 0; i < vg.ChildCount; i++) {
                    Collect(vg.GetChildAt(i));
                }
            }
        }
        foreach (View r in GetInspectableUiRoots(includeDialogs)) {
            Collect(r);
        }
        return matches;
    }

    static string ReadJsonStr(JsonElement el, string key, string fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement p) && p.ValueKind == JsonValueKind.String
            ? (p.GetString() ?? fallback)
            : fallback;

    static int ReadJsonInt(JsonElement el, string key, int fallback, int min, int max) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement p) && p.TryGetInt32(out int v)
            ? Math.Clamp(v, min, max)
            : Math.Clamp(fallback, min, max);

    static bool ReadJsonBool(JsonElement el, string key, bool fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement p) &&
        (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean()
            : fallback;

    static double ReadJsonDouble(JsonElement el, string key, double fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement p) && p.TryGetDouble(out double v)
            ? v
            : fallback;
}

