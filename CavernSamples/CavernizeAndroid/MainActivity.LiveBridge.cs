using System.Text.Json;

using Android.Graphics;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;

using File = System.IO.File;
using MemoryStream = System.IO.MemoryStream;

namespace CavernizeAndroid;

/// <summary>
/// Live Bridge v2 host integration on <see cref="MainActivity"/>: safely marshals UI inspection,
/// playback control, tab navigation, library queries, settings refresh, and downscaled screenshots
/// onto the Android UI thread with bounded timeouts.
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

    Task<string> ILiveBridgeAppHost.GetAppStateJsonAsync() => RunOnUiThreadWithTimeoutAsync(() => {
        return JsonSerializer.Serialize(new {
            uiHostAttached = true,
            activeTab,
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

    Task<string> ILiveBridgeAppHost.GetDspTelemetryJsonAsync() => RunOnUiThreadWithTimeoutAsync(() => {
        var rot = playerEngine?.ListenerRotation ?? default;
        var pos = playerEngine?.ListenerPosition ?? default;
        return JsonSerializer.Serialize(new {
            headphoneVirtualizer = playerEngine?.HeadphoneVirtualizer ?? false,
            environmentType = playerEngine?.EnvironmentType.ToString() ?? "",
            audioQuality = playerEngine?.AudioQuality.ToString() ?? "",
            distanceSimulation = playerEngine?.DistanceSimulation ?? false,
            listenerRotation = new { pitch = Math.Round(rot.X, 2), yaw = Math.Round(rot.Y, 2), roll = Math.Round(rot.Z, 2) },
            listenerPosition = new { x = Math.Round(pos.X, 2), y = Math.Round(pos.Y, 2), z = Math.Round(pos.Z, 2) },
            iso226Enabled = store?.Iso226Enabled ?? false,
            lgsrEnabled = store?.LgsrEnabled ?? false,
            loudnessNormMode = store?.LoudnessNormModeIndex ?? 0,
            headTrackingEnabled = store?.HeadTrackingEnabled ?? false,
            headTrackingAvailable = headTrackingManager != null,
            proAudioBridgeAvailable = CavernizeAndroid.Audio.NativeProAudioBridge.IsAvailable
        }, new JsonSerializerOptions { WriteIndented = true });
    });

    Task<string> ILiveBridgeAppHost.QueryLibraryJsonAsync(string query, int limit) => RunOnUiThreadWithTimeoutAsync(() => {
        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot ?? MediaLibraryService.TryLoadCachedSnapshot(this);
        var tracks = (snap?.Tracks ?? [])
            .Where(t =>
                string.IsNullOrWhiteSpace(query) ||
                (t.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
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
            query = query ?? "",
            tracks
        }, new JsonSerializerOptions { WriteIndented = true });
    });

    Task<string> ILiveBridgeAppHost.DumpUiTreeJsonAsync(int maxDepth) => RunOnUiThreadWithTimeoutAsync(() => {
        View root = rootFrameHost ?? Window?.DecorView;
        if (root == null) {
            return "{}";
        }
        int nodes = 0;
        object Walk(View v, int depth) {
            if (v == null || nodes >= 260) {
                return null;
            }
            nodes++;
            string txt = v is TextView tv ? (tv.Text ?? "") : "";
            if (txt.Length > 100) {
                txt = txt[..100] + "...";
            }
            string cd = v.ContentDescription ?? "";
            List<object> children = null;
            if (depth < maxDepth && v is ViewGroup vg && vg.ChildCount > 0) {
                children = [];
                for (int i = 0; i < vg.ChildCount && nodes < 260; i++) {
                    View ch = vg.GetChildAt(i);
                    if (ch != null && ch.Visibility == ViewStates.Visible) {
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
                w = v.Width,
                h = v.Height,
                children = children != null && children.Count > 0 ? children : null
            };
        }
        return JsonSerializer.Serialize(Walk(root, 0), new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    });

    Task<string> ILiveBridgeAppHost.CaptureUiScreenshotJsonAsync(int maxWidth, int quality) => RunOnUiThreadWithTimeoutAsync(() => {
        View root = Window?.DecorView?.RootView ?? rootFrameHost;
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
        View root = rootFrameHost ?? Window?.DecorView;
        if (root == null) {
            return (false, "Root view unavailable.");
        }
        List<View> matches = [];
        void Collect(View v) {
            if (v == null || v.Visibility != ViewStates.Visible) {
                return;
            }
            string txt = v is TextView tv ? (tv.Text ?? "") : "";
            string cd = v.ContentDescription ?? "";
            if ((txt.Contains(textQuery, StringComparison.OrdinalIgnoreCase) || cd.Contains(textQuery, StringComparison.OrdinalIgnoreCase)) &&
                v.Clickable && v.Enabled) {
                matches.Add(v);
            }
            if (v is ViewGroup vg) {
                for (int i = 0; i < vg.ChildCount; i++) {
                    Collect(vg.GetChildAt(i));
                }
            }
        }
        Collect(root);
        if (matches.Count == 0) {
            return (false, $"No visible clickable View found matching '{textQuery}'.");
        }
        int pick = Math.Clamp(index, 0, matches.Count - 1);
        bool clicked = matches[pick].PerformClick();
        return (clicked, clicked ? $"Clicked match #{pick} of {matches.Count} for '{textQuery}'." : $"PerformClick returned false for '{textQuery}'.");
    });

    Task ILiveBridgeAppHost.ApplySettingsChangedAsync() => RunOnUiThreadWithTimeoutAsync(() => {
        RefreshAllUiState();
        RefreshLogSummaryLabel();
        return true;
    });
}
