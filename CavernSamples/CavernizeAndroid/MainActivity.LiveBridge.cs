using System.Diagnostics;
using System.Text.Json;

using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
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

    async Task<string> ILiveBridgeAppHost.GetSoakSnapshotJsonAsync(bool forceGc) {
        (int views, int clickables, int dlgs, bool opRunning, int tab) = await RunOnUiThreadWithTimeoutAsync(() => {
            (int total, int clickable) = CountInspectableViews();
            return (total, clickable, OneUiModalDialog.GetActiveDialogs().Count, isOperationRunning, activeTab);
        }, 5000).ConfigureAwait(false);
        return AndroidRuntimeTelemetry.BuildSoakSnapshotJson(
            this,
            forceGc: forceGc,
            viewCount: views,
            clickableViewCount: clickables,
            activeDialogs: dlgs,
            isOperationRunning: opRunning,
            activeTab: tab);
    }

    (int totalViews, int clickableViews) CountInspectableViews() {
        int total = 0;
        int clickable = 0;
        void WalkCount(View v, int depth) {
            if (v == null || depth > 28) {
                return;
            }
            total++;
            if (v.Clickable && v.Enabled) {
                clickable++;
            }
            if (v is ViewGroup vg) {
                int cnt = vg.ChildCount;
                for (int i = 0; i < cnt; i++) {
                    WalkCount(vg.GetChildAt(i), depth + 1);
                }
            }
        }
        foreach (View r in GetInspectableUiRoots(includeDialogs: true)) {
            WalkCount(r, 0);
        }
        return (total, clickable);
    }

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

    static (int? bgArgb, float? cornerRadDp, bool customBg) InspectDrawableVisuals(Drawable bg, float density) {
        if (bg == null) {
            return (null, null, false);
        }
        if (bg is ColorDrawable cd) {
            int argb = cd.Color.ToArgb();
            return (Color.GetAlphaComponent(argb) > 0 ? argb : null, null, false);
        }
        if (bg is GradientDrawable gd) {
            int? col = null;
            try {
                if (gd.Color != null) {
                    int c = gd.Color.DefaultColor;
                    if (Color.GetAlphaComponent(c) > 0) {
                        col = c;
                    }
                }
                if (!col.HasValue) {
                    int[] cols = gd.GetColors();
                    if (cols != null && cols.Length > 0) {
                        int c0 = cols[0];
                        int c1 = cols[^1];
                        int a = (Color.GetAlphaComponent(c0) + Color.GetAlphaComponent(c1)) / 2;
                        if (a > 0) {
                            int r = (Color.GetRedComponent(c0) + Color.GetRedComponent(c1)) / 2;
                            int g = (Color.GetGreenComponent(c0) + Color.GetGreenComponent(c1)) / 2;
                            int b = (Color.GetBlueComponent(c0) + Color.GetBlueComponent(c1)) / 2;
                            col = Color.Argb(a, r, g, b);
                        }
                    }
                }
            } catch {
                // Ignore drawable color lookup edge cases
            }
            float? rad = null;
            try {
                float rPx = gd.CornerRadius;
                if (float.IsFinite(rPx) && rPx > 0.5f && density > 0f) {
                    rad = (float)Math.Round(rPx / density, 1);
                }
            } catch {
                // Ignore
            }
            return (col, rad, false);
        }
        if (bg is LayerDrawable ld && ld.NumberOfLayers > 0) {
            int? col = null;
            float? rad = null;
            bool hasCustom = false;
            int limit = Math.Min(ld.NumberOfLayers, 4);
            for (int i = 0; i < limit; i++) {
                // RippleDrawable inherits LayerDrawable and stores an opaque white mask at Android.Resource.Id.Mask;
                // never report the ripple hit mask as the visible background color.
                if (ld.GetId(i) == Android.Resource.Id.Mask) {
                    continue;
                }
                (int? lc, float? lr, bool lCustom) = InspectDrawableVisuals(ld.GetDrawable(i), density);
                col ??= lc;
                rad ??= lr;
                hasCustom |= lCustom;
                if (col.HasValue && rad.HasValue) {
                    break;
                }
            }
            return (col, rad, !col.HasValue && hasCustom);
        }
        if (bg is DrawableWrapper dw && dw.Drawable != null) {
            return InspectDrawableVisuals(dw.Drawable, density);
        }
        return (null, null, true);
    }

    static string GetScrollAxisTag(View v) => v switch {
        HorizontalScrollView => "h",
        ScrollView => "v",
        AbsListView => "v",
        _ => v.GetType().Name.Contains("Scroll", StringComparison.OrdinalIgnoreCase)
            ? (v.GetType().Name.Contains("Horizontal", StringComparison.OrdinalIgnoreCase) ? "h" : "v")
            : null
    };

    static string GetEllipsizeModeTag(TextUtils.TruncateAt ellipsize) {
        if (ellipsize == null) {
            return null;
        }
        if (ellipsize == TextUtils.TruncateAt.End) return "end";
        if (ellipsize == TextUtils.TruncateAt.Middle) return "middle";
        if (ellipsize == TextUtils.TruncateAt.Start) return "start";
        if (ellipsize == TextUtils.TruncateAt.Marquee) return "marquee";
        return ellipsize.ToString()?.ToLowerInvariant();
    }

    Task<string> ILiveBridgeAppHost.DumpUiTreeJsonAsync(int maxDepth, bool includeGeometry) => RunOnUiThreadWithTimeoutAsync(() => {
        EnsureTabLaidOut(activeTab);
        object snap = BuildUiTreeSnapshotObject(maxDepth, includeGeometry);
        return JsonSerializer.Serialize(snap, new JsonSerializerOptions {
            WriteIndented = !includeGeometry,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    });

    object BuildUiTreeSnapshotObject(
        int maxDepth,
        bool includeGeometry,
        View rootOverride = null,
        int? overrideDispX = null,
        int? overrideDispY = null,
        int? overrideDispW = null,
        int? overrideDispH = null,
        float? overrideFontScale = null,
        bool includeDialogs = true) {
        Stopwatch walkSw = Stopwatch.StartNew();
        int nodes = 0;
        const int maxNodes = 480;
        float density = Math.Max(0.75f, Resources?.DisplayMetrics?.Density ?? 3f);
        float fontScale = overrideFontScale ?? Math.Max(0.5f, Resources?.Configuration?.FontScale ?? 1f);
        int screenW = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        int screenH = Resources?.DisplayMetrics?.HeightPixels ?? 2400;
        View mainRoot = rootOverride ?? rootFrameHost ?? Window?.DecorView;
        int dispX = overrideDispX ?? 0;
        int dispY = overrideDispY ?? 0;
        int dispW = overrideDispW ?? (mainRoot != null && mainRoot.Width > 0 ? mainRoot.Width : screenW);
        int dispH = overrideDispH ?? (mainRoot != null && mainRoot.Height > 0 ? mainRoot.Height : screenH);
        int[] locBuf = new int[2];

        View activeTabContainer = (tabContainers != null && activeTab >= 0 && activeTab < tabContainers.Length)
            ? tabContainers[activeTab]
            : null;

        object Walk(View v, int depth, float cumScaleX, float cumScaleY) {
            if (v == null || nodes >= maxNodes) {
                return null;
            }
            float nextScaleX = cumScaleX * (float.IsFinite(v.ScaleX) && v.ScaleX > 0.01f ? v.ScaleX : 1f);
            float nextScaleY = cumScaleY * (float.IsFinite(v.ScaleY) && v.ScaleY > 0.01f ? v.ScaleY : 1f);
            if (activeTabContainer != null && tabContainers != null && v != activeTabContainer) {
                for (int ti = 0; ti < tabContainers.Length; ti++) {
                    if (ti != activeTab && ReferenceEquals(v, tabContainers[ti])) {
                        return null;
                    }
                }
            }
            nodes++;
            string txt = v is TextView tvRaw ? (tvRaw.Text ?? "") : "";
            string txtEmit = txt.Length > 100 ? txt[..100] + "..." : txt;
            string cd = v.ContentDescription ?? "";
            bool isClickable = v.Clickable && v.Enabled;
            bool? isChecked = v is CompoundButton cb ? cb.Checked : null;
            int? progress = v is SeekBar sb ? sb.Progress : (v is OneUiWaveMediaSeekBar osb ? osb.Progress : null);
            int? selectedIdx = v is OneUiInlineSelector sel ? sel.SelectedIndex : null;

            int? x = null;
            int? y = null;
            string scrollAxis = null;
            string orient = null;
            bool? baselineAligned = null;
            bool? clipCh = null;
            int? ell = null;
            string ellMode = null;
            int? maxL = null;
            int? measW = null;
            int? fg = null;
            int? bg = null;
            bool? customBg = null;
            float? sp = null;
            float? radDp = null;
            int[] padDp = null;
            bool? hasClick = null;

            if (includeGeometry) {
                v.GetLocationOnScreen(locBuf);
                x = locBuf[0];
                y = locBuf[1];
                scrollAxis = GetScrollAxisTag(v);
                if (v is LinearLayout ll) {
                    orient = ll.Orientation == Orientation.Horizontal ? "h" : "v";
                    if (ll.Orientation == Orientation.Horizontal && ll.BaselineAligned) {
                        baselineAligned = true;
                    }
                }
                if (v is ViewGroup vgClip && !vgClip.ClipChildren) {
                    clipCh = false;
                }
                if (isClickable && !v.HasOnClickListeners) {
                    hasClick = false;
                }
                bool hasCustomBg;
                (bg, radDp, hasCustomBg) = InspectDrawableVisuals(v.Background, density);
                if (v is OneUiFrostedCapsuleView fc && !bg.HasValue) {
                    Color glaze = fc.GlazeBottomColor ?? fc.GlazeTopColor ?? OneUiTheme.SurfaceFloatingBar;
                    bg = glaze.ToArgb();
                    hasCustomBg = false;
                }
                if (hasCustomBg) {
                    customBg = true;
                }
                if (v is TextView tv) {
                    if (!string.IsNullOrEmpty(txt)) {
                        fg = tv.CurrentTextColor;
                        sp = (float)Math.Round(tv.TextSize / (density * fontScale), 1);
                        ellMode = GetEllipsizeModeTag(tv.Ellipsize);
                        int ml = tv.MaxLines;
                        if (ml is >= 1 and <= 4) {
                            maxL = ml;
                        }
                        Android.Text.Layout layout = tv.Layout;
                        if (layout != null) {
                            int ellSum = 0;
                            int lc = layout.LineCount;
                            for (int li = 0; li < lc; li++) {
                                ellSum += layout.GetEllipsisCount(li);
                            }
                            if (ellSum > 0) {
                                ell = ellSum;
                            }
                        }
                        if (ml == 1 && v.Width > 0 && tv.Paint != null) {
                            float measured = tv.Paint.MeasureText(txt) + tv.CompoundPaddingLeft + tv.CompoundPaddingRight;
                            if (float.IsFinite(measured) && measured > v.Width + 2f) {
                                measW = (int)Math.Ceiling(measured);
                            }
                        }
                    }
                }
                if (v.PaddingLeft > 0 || v.PaddingTop > 0 || v.PaddingRight > 0 || v.PaddingBottom > 0) {
                    if (v is Button || v is LinearLayout || v is FrameLayout) {
                        padDp = [
                            (int)Math.Round(v.PaddingLeft / density),
                            (int)Math.Round(v.PaddingTop / density),
                            (int)Math.Round(v.PaddingRight / density),
                            (int)Math.Round(v.PaddingBottom / density)
                        ];
                    }
                }
            }

            List<object> children = null;
            if (depth < maxDepth && v is ViewGroup vg && vg.ChildCount > 0) {
                children = [];
                for (int i = 0; i < vg.ChildCount && nodes < maxNodes; i++) {
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
                        object cNode = Walk(ch, depth + 1, nextScaleX, nextScaleY);
                        if (cNode != null) {
                            children.Add(cNode);
                        }
                    }
                }
            }
            int emitW = includeGeometry ? Math.Max(0, (int)Math.Round(v.Width * nextScaleX)) : v.Width;
            int emitH = includeGeometry ? Math.Max(0, (int)Math.Round(v.Height * nextScaleY)) : v.Height;
            return new {
                type = v.GetType().Name,
                clickable = isClickable ? (bool?)true : null,
                enabled = v.Enabled ? null : (bool?)false,
                hasClick,
                text = string.IsNullOrEmpty(txtEmit) ? null : txtEmit,
                desc = string.IsNullOrEmpty(cd) ? null : cd,
                @checked = isChecked,
                progress,
                selectedIdx,
                x,
                y,
                w = emitW,
                h = emitH,
                scrollAxis,
                orient,
                baselineAligned,
                clipCh,
                ell,
                ellMode,
                maxL,
                measW,
                fg,
                bg,
                customBg,
                sp,
                radDp,
                padDp,
                children = children != null && children.Count > 0 ? children : null
            };
        }

        List<object> dialogTrees = [];
        if (includeDialogs) {
            IReadOnlyList<Dialog> activeDlgs = OneUiModalDialog.GetActiveDialogs();
            for (int i = 0; i < activeDlgs.Count; i++) {
                View dv = activeDlgs[i]?.Window?.DecorView;
                if (dv != null) {
                    dialogTrees.Add(Walk(dv, 0, 1f, 1f));
                }
            }
        }
        object activityTree = mainRoot != null ? Walk(mainRoot, 0, 1f, 1f) : null;
        walkSw.Stop();

        return new {
            schemaVersion = includeGeometry ? 2 : 1,
            activeTab,
            activeDialogs = dialogTrees.Count,
            dpDensity = includeGeometry ? (float?)Math.Round(density, 2) : null,
            fontScale = includeGeometry ? (float?)Math.Round(fontScale, 2) : null,
            displayRect = includeGeometry ? new { x = dispX, y = dispY, w = dispW, h = dispH, screenW, screenH } : null,
            walkMs = includeGeometry ? (double?)Math.Round(walkSw.Elapsed.TotalMilliseconds, 2) : null,
            nodeCount = nodes,
            designTokens = includeGeometry ? new {
                cardCornerDp = OneUiTheme.EffectiveCardCornerDp,
                buttonCornerDp = OneUiTheme.EffectiveButtonCornerDp,
                tileCornerDp = OneUiTheme.EffectiveTileCornerDp,
                bgScreen = OneUiTheme.BgScreen.ToArgb(),
                surfaceCard = OneUiTheme.SurfaceCard.ToArgb(),
                textPrimary = OneUiTheme.TextPrimary.ToArgb(),
                textSecondary = OneUiTheme.TextSecondary.ToArgb(),
                primary = OneUiTheme.Primary.ToArgb()
            } : null,
            dialogs = dialogTrees.Count > 0 ? dialogTrees : null,
            activity = activityTree
        };
    }

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

        // Deterministic downsampled BT.709 grayscale grid (48px wide) for zero-dep Golden SSIM visual diff
        int lumaW = 48;
        int lumaH = Math.Clamp((int)Math.Round((double)dstH * lumaW / Math.Max(1, dstW)), 32, 128);
        byte[] lumaBytes = new byte[lumaW * lumaH];
        using (Bitmap small = Bitmap.CreateScaledBitmap(bmp, lumaW, lumaH, true)) {
            int[] pxBuf = new int[lumaW * lumaH];
            small.GetPixels(pxBuf, 0, lumaW, 0, 0, lumaW, lumaH);
            for (int i = 0; i < pxBuf.Length; i++) {
                int argb = pxBuf[i];
                int r = (argb >> 16) & 0xFF;
                int g = (argb >> 8) & 0xFF;
                int b = argb & 0xFF;
                int yVal = (int)Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b);
                lumaBytes[i] = (byte)Math.Clamp(yVal, 0, 255);
            }
        }

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
            lumaW,
            lumaH,
            lumaB64 = Convert.ToBase64String(lumaBytes),
            imageBase64 = b64
        });
    });

    Task<(bool ok, string message)> ILiveBridgeAppHost.SwitchTabAsync(int tabIndex, string tabName) => RunOnUiThreadWithTimeoutAsync(() => {
        int target = tabIndex;
        if (target < 0 && !string.IsNullOrWhiteSpace(tabName)) {
            target = tabName.Trim().ToLowerInvariant() switch {
                "library" or "player" or "music" or "play" => 0,
                "sound" or "settings" or "config" or "room" or "3d" => 1,
                "export" or "studio" or "estudio" or "convert" or "stems" or "tracks" => 2,
                _ => -1
            };
        }
        int maxTab = LiveDiagnosticBridgeService.MaxTabIndex;
        if (target < 0 || target > maxTab) {
            return (false, $"Invalid tab index/name (valid: 0..{maxTab} or {string.Join("/", LiveDiagnosticBridgeService.CanonicalTabNames)}).");
        }
        SwitchTab(target);
        SettleActiveUiAnimationsForAudit(target);
        EnsureTabLaidOut(target);
        return (true, $"Switched to tab {target}.");
    });

    void SettleActiveUiAnimationsForAudit(int? targetTab = null) {
        if (tabStripAnimator != null) {
            try { tabStripAnimator.Cancel(); } catch { }
            try { tabStripAnimator.Dispose(); } catch { }
            tabStripAnimator = null;
        }
        int settledTab = targetTab ?? activeTab;
        if (tabContainers != null) {
            for (int i = 0; i < tabContainers.Length; i++) {
                View tc = tabContainers[i];
                if (tc == null) continue;
                tc.Animate()?.Cancel();
                tc.TranslationX = 0f;
                tc.TranslationY = 0f;
                tc.ScaleX = 1f;
                tc.ScaleY = 1f;
                tc.Alpha = 1f;
                tc.Visibility = i == settledTab ? ViewStates.Visible : ViewStates.Gone;
            }
        }
        if (navFrostedHost != null) {
            navVisAnimGen++;
            navFrostedHost.Animate()?.Cancel();
            bool shouldShowNav = isBottomNavVisible;
            navFrostedHost.Visibility = shouldShowNav ? ViewStates.Visible : ViewStates.Gone;
            navFrostedHost.Alpha = shouldShowNav ? 1f : 0f;
            navFrostedHost.TranslationX = 0f;
            navFrostedHost.TranslationY = 0f;
            navFrostedHost.ScaleX = 1f;
            navFrostedHost.ScaleY = 1f;
        }
        if (bottomScrimView != null) {
            bottomScrimView.Animate()?.Cancel();
            bottomScrimView.Visibility = isBottomNavVisible ? ViewStates.Visible : ViewStates.Gone;
            bottomScrimView.Alpha = isBottomNavVisible ? 1f : 0f;
        }
        if (tabIndicatorPill != null) {
            tabIndicatorPill.Animate()?.Cancel();
            PositionTabIndicator(animate: false);
        }
        IReadOnlyList<Dialog> dlgs = OneUiModalDialog.GetActiveDialogs();
        for (int i = 0; i < dlgs.Count; i++) {
            View dv = dlgs[i]?.Window?.DecorView;
            if (dv != null) {
                SettleViewSubtreeAnimations(dv, 0);
            }
        }
    }

    static void SettleViewSubtreeAnimations(View v, int depth) {
        if (v == null || depth > 8) {
            return;
        }
        v.Animate()?.Cancel();
        if (MathF.Abs(v.ScaleX - 1f) > 0.001f && v is not RoundedArtworkView) {
            v.ScaleX = 1f;
        }
        if (MathF.Abs(v.ScaleY - 1f) > 0.001f && v is not RoundedArtworkView) {
            v.ScaleY = 1f;
        }
        if (v.Alpha < 0.99f && v.Visibility == ViewStates.Visible) {
            v.Alpha = 1f;
        }
        if (v is ViewGroup vg) {
            for (int i = 0; i < vg.ChildCount; i++) {
                SettleViewSubtreeAnimations(vg.GetChildAt(i), depth + 1);
            }
        }
    }

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

    async Task<(bool ok, string json)> ILiveBridgeAppHost.UiInteractAsync(JsonElement argsEl) {
        string topAction = ReadJsonStr(argsEl, "action", "find").Trim().ToLowerInvariant();
        if (topAction == "rage_click") {
            return await ExecuteRageClickAsync(argsEl).ConfigureAwait(false);
        }
        if (topAction == "drag_sweep") {
            return await ExecuteDragSweepAsync(argsEl).ConfigureAwait(false);
        }
        return await RunOnUiThreadWithTimeoutAsync(() => {
            string action = topAction;
            string query = ReadJsonStr(argsEl, "query", ReadJsonStr(argsEl, "text", ""));
            string typeFilter = ReadJsonStr(argsEl, "type", "");
            int index = ReadJsonInt(argsEl, "index", 0, 0, 200);
            bool includeDialogs = ReadJsonBool(argsEl, "includeDialogs", true);

            switch (action) {
                case "reflow_probe":
                    return ExecuteReflowProbeOnUiThread(argsEl);
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
                if (activeTab == 1 && activeSettingsCategory >= 0) {
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
                return (false, $"Unknown ui_interact action '{action}'. Valid: find, click, long_click, rage_click, drag_sweep, reflow_probe, set_text, set_checked, set_progress, select_item, scroll_by, tap_xy, swipe_xy, back, dismiss_dialogs, dump_dialogs.");
        }
        }).ConfigureAwait(false);
    }

    async Task<(bool ok, string json)> ExecuteRageClickAsync(JsonElement argsEl) {
        string query = ReadJsonStr(argsEl, "query", ReadJsonStr(argsEl, "text", ""));
        string typeFilter = ReadJsonStr(argsEl, "type", "");
        int index = ReadJsonInt(argsEl, "index", 0, 0, 200);
        int count = ReadJsonInt(argsEl, "count", 8, 2, 16);
        int intervalMs = ReadJsonInt(argsEl, "intervalMs", 25, 8, 150);
        bool includeDialogs = ReadJsonBool(argsEl, "includeDialogs", true);

        int succeeded = 0;
        int failed = 0;
        double maxBlockMs = 0.0;
        List<string> exceptions = [];
        string targetType = "";
        Stopwatch totalSw = Stopwatch.StartNew();

        for (int i = 0; i < count; i++) {
            (bool clicked, double blockMs, string tType, string err) = await RunOnUiThreadWithTimeoutAsync(() => {
                Stopwatch sw = Stopwatch.StartNew();
                try {
                    List<View> matches = FindViews(query, typeFilter, clickableOnly: true, includeDialogs);
                    if (matches.Count == 0) {
                        matches = FindViews(query, typeFilter, clickableOnly: false, includeDialogs);
                    }
                    if (matches.Count == 0) {
                        sw.Stop();
                        return (false, sw.Elapsed.TotalMilliseconds, "", $"No visible View found matching query='{query}' type='{typeFilter}'.");
                    }
                    View target = matches[Math.Clamp(index, 0, matches.Count - 1)];
                    bool ok = target.PerformClick();
                    sw.Stop();
                    return (ok, sw.Elapsed.TotalMilliseconds, target.GetType().Name, "");
                } catch (Exception ex) {
                    sw.Stop();
                    return (false, sw.Elapsed.TotalMilliseconds, "", $"{ex.GetType().Name}: {ex.Message}");
                }
            }, 6000).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(tType)) {
                targetType = tType;
            }
            if (blockMs > maxBlockMs) {
                maxBlockMs = blockMs;
            }
            if (!string.IsNullOrEmpty(err)) {
                failed++;
                if (exceptions.Count < 5) {
                    exceptions.Add(err);
                }
            } else if (clicked) {
                succeeded++;
            } else {
                failed++;
            }
            if (i + 1 < count) {
                await Task.Delay(intervalMs).ConfigureAwait(false);
            }
        }
        totalSw.Stop();

        (int activeDlgs, bool opRunning) = await RunOnUiThreadWithTimeoutAsync(() =>
            (OneUiModalDialog.GetActiveDialogs().Count, isOperationRunning), 4000).ConfigureAwait(false);

        bool overallOk = succeeded > 0 && exceptions.Count == 0;
        return (overallOk, JsonSerializer.Serialize(new {
            action = "rage_click",
            ok = overallOk,
            query,
            type = targetType,
            requested = count,
            intervalMs,
            succeeded,
            failed,
            maxBlockMs = Math.Round(maxBlockMs, 2),
            totalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            activeDialogsAfter = activeDlgs,
            isOperationRunningAfter = opRunning,
            exceptions
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    async Task<(bool ok, string json)> ExecuteDragSweepAsync(JsonElement argsEl) {
        string query = ReadJsonStr(argsEl, "query", ReadJsonStr(argsEl, "text", ""));
        string typeFilter = ReadJsonStr(argsEl, "type", "");
        int index = ReadJsonInt(argsEl, "index", 0, 0, 200);
        int steps = ReadJsonInt(argsEl, "steps", 24, 6, 60);
        int stepMs = ReadJsonInt(argsEl, "stepMs", 14, 8, 40);
        float fromRatio = Math.Clamp((float)ReadJsonDouble(argsEl, "fromRatio", 0.12), 0.02f, 0.98f);
        float toRatio = Math.Clamp((float)ReadJsonDouble(argsEl, "toRatio", 0.88), 0.02f, 0.98f);
        bool restoreProgress = ReadJsonBool(argsEl, "restoreProgress", true);
        bool includeDialogs = ReadJsonBool(argsEl, "includeDialogs", true);

        (bool found, View target, string targetType, int startProg, int width, int height) = await RunOnUiThreadWithTimeoutAsync(() => {
            List<View> matches = FindViews(query, typeFilter, clickableOnly: false, includeDialogs)
                .Where(v => v is SeekBar or OneUiWaveMediaSeekBar)
                .ToList();
            if (matches.Count == 0) {
                return (false, (View)null, "", 0, 0, 0);
            }
            View v = matches[Math.Clamp(index, 0, matches.Count - 1)];
            int prog = v is OneUiWaveMediaSeekBar osb ? osb.Progress : (v is SeekBar sb ? sb.Progress : 0);
            return (true, v, v.GetType().Name, prog, v.Width, v.Height);
        }, 5000).ConfigureAwait(false);

        if (!found || target == null || width <= 4 || height <= 2) {
            return (false, JsonSerializer.Serialize(new {
                action = "drag_sweep",
                ok = false,
                error = $"No visible SeekBar/OneUiWaveMediaSeekBar found matching query='{query}' type='{typeFilter}'."
            }));
        }

        long downTime = SystemClock.UptimeMillis();
        float yMid = height * 0.5f;
        float xStart = width * fromRatio;
        float xEnd = width * toRatio;
        double maxStepBlockMs = 0.0;
        int dispatched = 0;
        string sweepErr = "";
        int endProg = startProg;

        try {
            double downBlock = await RunOnUiThreadWithTimeoutAsync(() => {
                Stopwatch sw = Stopwatch.StartNew();
                using MotionEvent down = MotionEvent.Obtain(downTime, SystemClock.UptimeMillis(), MotionEventActions.Down, xStart, yMid, 0);
                target.DispatchTouchEvent(down);
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }, 4000).ConfigureAwait(false);
            maxStepBlockMs = Math.Max(maxStepBlockMs, downBlock);
            dispatched++;

            for (int s = 1; s <= steps; s++) {
                await Task.Delay(stepMs).ConfigureAwait(false);
                float t = s / (float)steps;
                // Sweep forward then halfway back so both increase & decrease paths get stressed
                float x = t <= 0.65f
                    ? xStart + (xEnd - xStart) * (t / 0.65f)
                    : xEnd - (xEnd - xStart) * ((t - 0.65f) / 0.35f) * 0.5f;
                double blk = await RunOnUiThreadWithTimeoutAsync(() => {
                    Stopwatch sw = Stopwatch.StartNew();
                    using MotionEvent mv = MotionEvent.Obtain(downTime, SystemClock.UptimeMillis(), MotionEventActions.Move, x, yMid, 0);
                    target.DispatchTouchEvent(mv);
                    sw.Stop();
                    return sw.Elapsed.TotalMilliseconds;
                }, 4000).ConfigureAwait(false);
                if (blk > maxStepBlockMs) {
                    maxStepBlockMs = blk;
                }
                dispatched++;
            }

            (double upBlock, int curProg) = await RunOnUiThreadWithTimeoutAsync(() => {
                Stopwatch sw = Stopwatch.StartNew();
                float xFinal = xStart + (xEnd - xStart) * 0.5f;
                using MotionEvent up = MotionEvent.Obtain(downTime, SystemClock.UptimeMillis(), MotionEventActions.Up, xFinal, yMid, 0);
                target.DispatchTouchEvent(up);
                sw.Stop();
                int p = target is OneUiWaveMediaSeekBar osb ? osb.Progress : (target is SeekBar sb ? sb.Progress : 0);
                return (sw.Elapsed.TotalMilliseconds, p);
            }, 4000).ConfigureAwait(false);
            maxStepBlockMs = Math.Max(maxStepBlockMs, upBlock);
            endProg = curProg;
            dispatched++;
        } catch (Exception ex) {
            sweepErr = $"{ex.GetType().Name}: {ex.Message}";
        } finally {
            if (restoreProgress) {
                try {
                    await RunOnUiThreadWithTimeoutAsync(() => {
                        if (target is OneUiWaveMediaSeekBar osb) {
                            osb.Progress = startProg;
                        } else if (target is SeekBar sb) {
                            sb.Progress = startProg;
                        }
                        return true;
                    }, 3000).ConfigureAwait(false);
                } catch {
                    // Best effort restore
                }
            }
        }

        bool ok = string.IsNullOrEmpty(sweepErr) && dispatched >= steps;
        return (ok, JsonSerializer.Serialize(new {
            action = "drag_sweep",
            ok,
            type = targetType,
            stepsRequested = steps,
            stepsDispatched = dispatched,
            stepMs,
            startProgress = startProg,
            endProgress = endProg,
            restoredProgress = restoreProgress,
            maxStepUiBlockMs = Math.Round(maxStepBlockMs, 2),
            error = sweepErr
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    (bool ok, string json) ExecuteReflowProbeOnUiThread(JsonElement argsEl) {
        string mode = ReadJsonStr(argsEl, "mode", "both").Trim().ToLowerInvariant();
        float fontScale = Math.Clamp((float)ReadJsonDouble(argsEl, "fontScale", 1.3), 1.05f, 2.0f);
        int maxDepth = ReadJsonInt(argsEl, "maxDepth", 12, 4, 16);

        EnsureTabLaidOut(activeTab);
        View targetRoot = (tabContainers != null && activeTab >= 0 && activeTab < tabContainers.Length && tabContainers[activeTab] != null)
            ? tabContainers[activeTab]
            : (rootFrameHost ?? Window?.DecorView);
        if (targetRoot == null || targetRoot.Width <= 0 || targetRoot.Height <= 0) {
            return (false, "Active tab root is not laid out for reflow_probe.");
        }

        int origW = targetRoot.Width;
        int origH = targetRoot.Height;
        int origLeft = targetRoot.Left;
        int origTop = targetRoot.Top;
        int origRight = targetRoot.Right;
        int origBottom = targetRoot.Bottom;
        int[] rootLoc = new int[2];
        targetRoot.GetLocationOnScreen(rootLoc);
        float baseFontScale = Math.Max(0.5f, Resources?.Configuration?.FontScale ?? 1f);

        object fontTree = null;
        object landTree = null;
        int scaledViews = 0;

        // 1. Reversible 1.3x font-scale reflow audit
        if (mode is "font_130" or "font" or "both") {
            List<(TextView tv, float origPx)> savedSizes = [];
            void CollectTextViews(View v) {
                if (v == null || v.Visibility != ViewStates.Visible) {
                    return;
                }
                if (v is TextView tv && !string.IsNullOrEmpty(tv.Text)) {
                    savedSizes.Add((tv, tv.TextSize));
                }
                if (v is ViewGroup vg) {
                    for (int i = 0; i < vg.ChildCount; i++) {
                        CollectTextViews(vg.GetChildAt(i));
                    }
                }
            }
            CollectTextViews(targetRoot);
            scaledViews = savedSizes.Count;
            try {
                foreach ((TextView tv, float origPx) in savedSizes) {
                    tv.SetTextSize(Android.Util.ComplexUnitType.Px, origPx * fontScale);
                }
                int wSpec = View.MeasureSpec.MakeMeasureSpec(origW, MeasureSpecMode.Exactly);
                int hSpec = View.MeasureSpec.MakeMeasureSpec(origH, MeasureSpecMode.Exactly);
                targetRoot.Measure(wSpec, hSpec);
                targetRoot.Layout(origLeft, origTop, origRight, origBottom);
                fontTree = BuildUiTreeSnapshotObject(
                    maxDepth,
                    includeGeometry: true,
                    rootOverride: targetRoot,
                    overrideDispX: rootLoc[0],
                    overrideDispY: rootLoc[1],
                    overrideDispW: origW,
                    overrideDispH: origH,
                    overrideFontScale: baseFontScale * fontScale,
                    includeDialogs: false);
            } finally {
                foreach ((TextView tv, float origPx) in savedSizes) {
                    tv.SetTextSize(Android.Util.ComplexUnitType.Px, origPx);
                }
                int wSpec = View.MeasureSpec.MakeMeasureSpec(origW, MeasureSpecMode.Exactly);
                int hSpec = View.MeasureSpec.MakeMeasureSpec(origH, MeasureSpecMode.Exactly);
                targetRoot.Measure(wSpec, hSpec);
                targetRoot.Layout(origLeft, origTop, origRight, origBottom);
            }
        }

        // 2. Reversible landscape orientation reflow audit (swapped viewport aspect ratio)
        int landW = Math.Max(origW, origH);
        int landH = Math.Min(origW, origH);
        if (mode is "landscape" or "orientation" or "both") {
            try {
                int wSpec = View.MeasureSpec.MakeMeasureSpec(landW, MeasureSpecMode.Exactly);
                int hSpec = View.MeasureSpec.MakeMeasureSpec(landH, MeasureSpecMode.Exactly);
                targetRoot.Measure(wSpec, hSpec);
                targetRoot.Layout(origLeft, origTop, origLeft + landW, origTop + landH);
                int[] landLoc = new int[2];
                targetRoot.GetLocationOnScreen(landLoc);
                landTree = BuildUiTreeSnapshotObject(
                    maxDepth,
                    includeGeometry: true,
                    rootOverride: targetRoot,
                    overrideDispX: landLoc[0],
                    overrideDispY: landLoc[1],
                    overrideDispW: landW,
                    overrideDispH: landH,
                    overrideFontScale: baseFontScale,
                    includeDialogs: false);
            } finally {
                int wSpec = View.MeasureSpec.MakeMeasureSpec(origW, MeasureSpecMode.Exactly);
                int hSpec = View.MeasureSpec.MakeMeasureSpec(origH, MeasureSpecMode.Exactly);
                targetRoot.Measure(wSpec, hSpec);
                targetRoot.Layout(origLeft, origTop, origRight, origBottom);
            }
        }

        return (true, JsonSerializer.Serialize(new {
            action = "reflow_probe",
            ok = true,
            mode,
            activeTab,
            fontScale,
            scaledTextViews = scaledViews,
            portraitBounds = new { w = origW, h = origH },
            landscapeBounds = new { w = landW, h = landH },
            restored = true,
            font_130 = fontTree,
            landscape = landTree
        }, new JsonSerializerOptions {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));
    }

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

