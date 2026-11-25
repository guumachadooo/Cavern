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
    void RebuildInlineTrackList() {
        foreach (WorkspaceSourceBar ws in workspaceSourceBars) {
            if (ws?.InlineTrackList == null || ws.TrackBadge == null) {
                continue;
            }
            ws.InlineTrackList.RemoveAllViews();
            if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
                ws.TrackBadge.Visibility = ViewStates.Gone;
                ws.InlineTrackList.Visibility = ViewStates.Gone;
                continue;
            }

            int selIdx = Math.Max(0, LoadedFile.Tracks.ToList().IndexOf(selectedTrack));
            ws.TrackBadge.Visibility = ViewStates.Visible;
            ws.TrackBadgeLabel.Text = $"{selIdx + 1}/{LoadedFile.Tracks.Count}";

            TextView hdr = Label(S["SelectTrackHeader"], 11.5f, ColMuted);
            hdr.SetPadding(Dp(2), 0, 0, Dp(6));
            ws.InlineTrackList.AddView(hdr);

            for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
                CavernizeTrack tr = LoadedFile.Tracks[i];
                bool isSel = tr == selectedTrack;

                LinearLayout row = HRow();
                row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 72), 14f)
                    : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
                OneUiTheme.AttachRecoil(row, 0.98f);

                View dot = new(this) {
                    Background = isSel
                        ? OneUiTheme.CreateRoundedDrawable(this, ColAccent, 99f, ColText, 2.5f)
                        : OneUiTheme.CreateRoundedDrawable(this, Color.Transparent, 99f, ColMuted, 1.5f)
                };

                TextView nameTv = Label($"#{i + 1}: {tr}", 12.5f, isSel ? ColText : OneUiTheme.TextSecondary);
                nameTv.SetIncludeFontPadding(false);
                nameTv.SetTypeface(null, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);

                row.AddView(dot, new LinearLayout.LayoutParams(Dp(14), Dp(14)) { RightMargin = Dp(8) });
                row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                row.Click += (_, _) => {
                    fileDetailsExpanded = false;
                    if (SelectedTrack != tr) {
                        SelectedTrack = tr;
                    } else {
                        UpdateInlineTrackListVisibility();
                    }
                };
                ws.InlineTrackList.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(4)
                });
            }
        }
        UpdateInlineTrackListVisibility();
    }

    void UpdateInlineTrackListVisibility() {
        bool hasFile = LoadedFile != null;
        bool showDrawer = fileDetailsExpanded && hasFile;
        foreach (WorkspaceSourceBar ws in workspaceSourceBars) {
            if (ws?.DetailsDrawer == null || ws.Chevron == null) {
                continue;
            }
            ws.Chevron.Visibility = hasFile ? ViewStates.Visible : ViewStates.Gone;
            SetExpandedWithAnimation(ws.DetailsDrawer, showDrawer);
            if (ws.InlineTrackList != null) {
                ws.InlineTrackList.Visibility = (showDrawer && LoadedFile.Tracks.Count > 1) ? ViewStates.Visible : ViewStates.Gone;
            }
            ws.Chevron.IconType = showDrawer ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
        }
    }

    void OpenMediaWithExplorer(Action<string> onSelected = null, int? switchToTab = null, bool autoPlay = false) {
        string startDir = !string.IsNullOrEmpty(LoadedFile?.Path)
            ? Path.GetDirectoryName(LoadedFile.Path)
            : store.LastExplorerDirectory;
        OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
            Title = S["ExplorerTitle"],
            Mode = ExplorerPickMode.SingleFile,
            InitialDirectory = startDir,
            OnConfirm = paths => {
                if (paths == null || paths.Count == 0) {
                    return;
                }
                string path = paths[0];
                store.LastExplorerDirectory = Path.GetDirectoryName(path) ?? "";
                if (onSelected != null) {
                    onSelected(path);
                } else {
                    OpenLocalPathWithFallbackAsync(path, switchToTab, autoPlay);
                }
            },
            OnFallbackSystemPicker = () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false)
        });
    }

    void OpenMultiFilesWithExplorer(Action<IReadOnlyList<string>> onSelected) {
        OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
            Title = S["ExplorerTitleMultiFiles"],
            Mode = ExplorerPickMode.MultiFile,
            InitialDirectory = store.LastExplorerDirectory,
            OnConfirm = paths => {
                if (paths != null && paths.Count > 0) {
                    store.LastExplorerDirectory = Path.GetDirectoryName(paths[0]) ?? "";
                    onSelected?.Invoke(paths);
                }
            },
            OnFallbackSystemPicker = () => LaunchFilePicker(ReqBatchMedia, allowMultiple: true)
        });
    }

    void OpenFileWithExplorerForExtensions(string title, IEnumerable<string> extensions, Action<string> onSelected, int fallbackRequestCode, bool allowAllFiles = false) {
        HashSet<string> allowed = extensions != null
            ? new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase)
            : null;
        OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
            Title = string.IsNullOrWhiteSpace(title) ? S["ExplorerTitle"] : title,
            Mode = ExplorerPickMode.SingleFile,
            InitialDirectory = store.LastExplorerDirectory,
            AllowedExtensions = allowed,
            AllowAllFiles = allowAllFiles,
            OnConfirm = paths => {
                if (paths != null && paths.Count > 0) {
                    store.LastExplorerDirectory = Path.GetDirectoryName(paths[0]) ?? "";
                    onSelected?.Invoke(paths[0]);
                }
            },
            OnFallbackSystemPicker = () => LaunchFilePicker(fallbackRequestCode, allowMultiple: false)
        });
    }

    void ShowToast(string msg) {
        if (!string.IsNullOrEmpty(msg) && !uiDestroyed) {
            Toast.MakeText(this, msg, ToastLength.Short).Show();
        }
    }

    void ShowTechnicalStreamDialog() {
        if (LoadedFile == null || selectedTrack == null) {
            ShowToast(S["ActiveSourceEmpty"]);
            return;
        }
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        TextView pathTv = Label(LoadedFile.Path ?? "", 11f, OneUiTheme.TextMuted);
        pathTv.SetPadding(0, 0, 0, Dp(8));
        content.AddView(pathTv);

        string details = string.Join("\n", selectedTrack.Details.Select(d => $"• {d.property}: {d.value}"));
        TextView techTv = Label($"{selectedTrack.FormatHeader}\n\n{details}", 12f, OneUiTheme.TextSecondary);
        techTv.SetPadding(0, 0, 0, Dp(12));
        content.AddView(techTv);

        if (LoadedFile.Tracks.Count > 1) {
            TextView hdr = Label(S["SelectTrackHeader"], 12.5f, OneUiTheme.TextPrimary);
            hdr.Typeface = OneUiTheme.GetAppleFontBold(this);
            hdr.SetPadding(0, Dp(4), 0, Dp(8));
            content.AddView(hdr);

            Android.App.Dialog activeDlg = null;
            for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
                CavernizeTrack tr = LoadedFile.Tracks[i];
                bool isSel = tr == selectedTrack;
                LinearLayout row = HRow();
                row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 14f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                OneUiTheme.AttachRecoil(row, 0.98f);
                TextView nameTv = Label($"#{i + 1}: {tr}", 12.5f, isSel ? ColText : OneUiTheme.TextSecondary);
                nameTv.Typeface = isSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                row.Click += (_, _) => {
                    activeDlg?.Dismiss();
                    if (SelectedTrack != tr) {
                        SelectedTrack = tr;
                    }
                };
                content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            activeDlg = OneUiModalDialog.Show(
                this,
                S["PlayerTechInfo"],
                customContent: content,
                icon: OneUiIconType.Info,
                iconBadgeColor: OneUiTheme.Tertiary,
                positiveText: S["CloseBtn"]);
            return;
        }

        OneUiModalDialog.Show(
            this,
            S["PlayerTechInfo"],
            customContent: content,
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: S["CloseBtn"]);
    }

    static Android.Views.Animations.PathInterpolator CreateOneUiEase() =>
        new(0.22f, 1f, 0.36f, 1f);

    void SetExpandedWithAnimation(View panel, bool expanded) {
        if (panel == null) {
            return;
        }
        bool isCurrentlyVisible = panel.Visibility == ViewStates.Visible;
        if (isCurrentlyVisible == expanded) {
            return;
        }
        panel.Animate()?.Cancel();
        if (panel.Parent is ViewGroup vg) {
            Android.Transitions.TransitionSet ts = new();
            ts.SetOrdering(Android.Transitions.TransitionOrdering.Together);
            ts.AddTransition(new Android.Transitions.ChangeBounds());
            ts.AddTransition(new Android.Transitions.Fade());
            ts.SetDuration(240);
            ts.SetInterpolator(CreateOneUiEase());
            Android.Transitions.TransitionManager.BeginDelayedTransition(vg, ts);
        }
        panel.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;
        panel.Alpha = 1f;
        panel.TranslationY = 0f;
    }

    bool ShouldShowSettingsDot() =>
        latestUpdateInfo?.IsUpdateAvailable == true;

    ValueAnimator tabStripAnimator;

    void ClearAllTabMotionBlur() {
        if (tabContainers == null) {
            return;
        }
        for (int i = 0; i < tabContainers.Length; i++) {
            if (tabContainers[i] != null) {
                OneUiMotionBlur.ClearMotionBlur(tabContainers[i]);
            }
        }
    }

    void SwitchTab(int idx) => SwitchTabInternal(idx, startOffsetPx: 0f);

    void SwitchTabInternal(int idx, float startOffsetPx) {
        if (tabContainers == null || idx < 0 || idx >= tabContainers.Length) {
            return;
        }
        int prevTab = activeTab;
        activeTab = idx;
        if (idx != 0) {
            musicVideoStage?.PauseVideoDecoderOnly();
        } else if (musicStageMode == PlayerStageMode.Video && !isLibrarySectionOpen) {
            musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0);
        } else if (musicStageMode == PlayerStageMode.Lyrics && !isLibrarySectionOpen) {
            musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
        }

        int myGen = ++tabSwitchAnimGen;
        tabStripAnimator?.Cancel();
        tabStripAnimator = null;
        ClearAllTabMotionBlur();

        if (prevTab != idx) {
            int low = Math.Min(prevTab, idx);
            int high = Math.Max(prevTab, idx);
            float viewportW = tabContainers[idx]?.Parent is View pv && pv.Width > 0
                ? pv.Width
                : (Resources?.DisplayMetrics?.WidthPixels ?? 1080);
            float density = Resources?.DisplayMetrics?.Density ?? 3f;

            // Compute continuous strip start index taking any live drag translation into account
            float startStripPos = prevTab - (viewportW > 1f ? startOffsetPx / viewportW : 0f);
            float endStripPos = idx;
            bool allowBlur = AndroidRuntimeTelemetry.GetThermalStatusCode(ApplicationContext ?? this) < 3;

            for (int i = 0; i < tabContainers.Length; i++) {
                View container = tabContainers[i];
                if (container == null) {
                    continue;
                }
                container.Animate()?.Cancel();
                container.Alpha = 1f;
                container.ScaleX = 1f;
                container.ScaleY = 1f;
                if (i >= low && i <= high) {
                    container.Visibility = ViewStates.Visible;
                    container.TranslationX = (i - startStripPos) * viewportW;
                } else {
                    container.Visibility = ViewStates.Gone;
                    container.TranslationX = 0f;
                }
            }

            float prevPos = startStripPos;
            long prevTimeMs = SystemClock.UptimeMillis();
            float smoothedBlurPx = 0f;
            const long FixedStripDurationMs = 310L; // Constant duration regardless of span -> 2x span = 2x speed = stronger motion blur!

            ValueAnimator anim = ValueAnimator.OfFloat(startStripPos, endStripPos);
            anim.SetDuration(FixedStripDurationMs);
            anim.SetInterpolator(new Android.Views.Animations.PathInterpolator(0.22f, 1f, 0.36f, 1f));
            anim.Update += (_, e) => {
                if (uiDestroyed || myGen != tabSwitchAnimGen) {
                    return;
                }
                float curPos = (float)(e.Animation?.AnimatedValue ?? endStripPos);
                long nowMs = SystemClock.UptimeMillis();
                float dtSec = Math.Max(0.004f, (nowMs - prevTimeMs) * 0.001f);
                float velPxPerSec = MathF.Abs(curPos - prevPos) * viewportW / dtSec;
                prevPos = curPos;
                prevTimeMs = nowMs;

                float targetBlur = allowBlur
                    ? OneUiMotionBlur.ComputeHorizontalBlurRadiusPx(velPxPerSec, density, maxBlurDp: 26f)
                    : 0f;
                // Smooth rise/decay envelope so motion blur feels optical and never pops sharply
                smoothedBlurPx = targetBlur > smoothedBlurPx
                    ? smoothedBlurPx + (targetBlur - smoothedBlurPx) * 0.55f
                    : smoothedBlurPx + (targetBlur - smoothedBlurPx) * 0.38f;
                if (smoothedBlurPx < 0.75f) {
                    smoothedBlurPx = 0f;
                }

                for (int i = low; i <= high; i++) {
                    View c = tabContainers[i];
                    if (c == null) {
                        continue;
                    }
                    float tx = (i - curPos) * viewportW;
                    c.TranslationX = tx;
                    bool onScreen = MathF.Abs(tx) < viewportW * 1.15f;
                    c.Visibility = onScreen ? ViewStates.Visible : ViewStates.Invisible;
                    if (onScreen && smoothedBlurPx >= 0.75f) {
                        OneUiMotionBlur.ApplyHorizontalMotionBlur(c, smoothedBlurPx);
                    } else {
                        OneUiMotionBlur.ClearMotionBlur(c);
                    }
                }
            };
            void FinalizeStrip() {
                if (uiDestroyed || myGen != tabSwitchAnimGen) {
                    return;
                }
                for (int i = 0; i < tabContainers.Length; i++) {
                    View c = tabContainers[i];
                    if (c == null) {
                        continue;
                    }
                    OneUiMotionBlur.ClearMotionBlur(c);
                    c.Alpha = 1f;
                    c.ScaleX = 1f;
                    c.ScaleY = 1f;
                    c.TranslationX = 0f;
                    c.Visibility = i == idx ? ViewStates.Visible : ViewStates.Gone;
                }
                tabStripAnimator = null;
                navFrostedHost?.RefreshBlur(force: true);
            }
            anim.AnimationEnd += (_, _) => FinalizeStrip();
            anim.AnimationCancel += (_, _) => ClearAllTabMotionBlur();
            tabStripAnimator = anim;
            anim.Start();
        } else if (tabContainers[idx] is View svCur) {
            svCur.Animate()?.Cancel();
            OneUiMotionBlur.ClearMotionBlur(svCur);
            svCur.Visibility = ViewStates.Visible;
            svCur.Alpha = 1f;
            svCur.ScaleX = 1f;
            svCur.ScaleY = 1f;
            svCur.TranslationX = 0f;
        }
        UpdateTabButtonStyles(animate: true);
        bool crossingPlayBoundary = prevTab != idx && ((prevTab == 0) != (idx == 0));
        if (idx == 0 && fileDetailsExpanded) {
            fileDetailsExpanded = false;
            UpdateInlineTrackListVisibility();
        }
        UpdateWorkspaceChromeVisibility(idx != 0, animate: crossingPlayBoundary);
        bool wantsNav = idx != 0 || isLibrarySectionOpen;
        SetBottomNavBarVisible(wantsNav, animate: prevTab != idx || isBottomNavVisible != wantsNav);
        if (idx == 1) {
            UpdateRoomCanvas();
            RefreshSpatialChoreoClarifierUi();
        } else if (idx == 0) {
            RefreshMusicPlayerUi();
        } else if (idx == 4) {
            if (prevTab == 4) {
                OpenSettingsCategory(-1);
            } else {
                RefreshSettingsCategorySubtitles();
            }
        }
        navFrostedHost?.RefreshBlur(force: true);
        SyncPredictiveBackRegistration();
    }

    /// <summary>
    /// Smart Chrome-Aware Top Bar Transition (One UI 9 Motion):
    /// Stays 100% stationary across non-Play tabs (3D Room / Studio / Stems / Settings) and
    /// animates strictly when crossing the Play tab boundary (0 &lt;-&gt; 1..4).
    /// </summary>
    void UpdateWorkspaceChromeVisibility(bool show, bool animate) {
        if (workspaceChromeHost == null) {
            return;
        }
        bool currentlyVisible = workspaceChromeHost.Visibility == ViewStates.Visible && workspaceChromeHost.Alpha > 0.98f;
        if (currentlyVisible == show && !animate) {
            return;
        }
        int myGen = ++workspaceChromeAnimGen;
        workspaceChromeHost.Animate()?.Cancel();

        if (!animate) {
            workspaceChromeHost.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            workspaceChromeHost.Alpha = show ? 1f : 0f;
            workspaceChromeHost.TranslationY = 0f;
            return;
        }

        if (show) {
            if (workspaceChromeHost.Visibility != ViewStates.Visible) {
                workspaceChromeHost.Visibility = ViewStates.Visible;
                workspaceChromeHost.Alpha = 0f;
                workspaceChromeHost.TranslationY = -Dp(12);
            }
            workspaceChromeHost.Animate()
                .Alpha(1f)
                .TranslationY(0f)
                .SetDuration(260)
                .SetInterpolator(CreateOneUiEase())
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != workspaceChromeAnimGen) return;
                    workspaceChromeHost.Alpha = 1f;
                    workspaceChromeHost.TranslationY = 0f;
                }))
                .Start();
        } else {
            workspaceChromeHost.Animate()
                .Alpha(0f)
                .TranslationY(-Dp(10))
                .SetDuration(185)
                .SetInterpolator(new Android.Views.Animations.AccelerateInterpolator(1.25f))
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != workspaceChromeAnimGen) return;
                    workspaceChromeHost.Visibility = ViewStates.Gone;
                    workspaceChromeHost.Alpha = 1f;
                    workspaceChromeHost.TranslationY = 0f;
                }))
                .Start();
        }
    }

    void SetBottomNavBarVisible(bool visible, bool animate) {
        if (navFrostedHost == null) {
            return;
        }
        isBottomNavVisible = visible;
        bool imeOpen = insetImeBottomPx > insetBarsBottomPx + Dp(48);
        if (visible && imeOpen) {
            navFrostedHost.Animate()?.Cancel();
            navFrostedHost.Visibility = ViewStates.Gone;
            if (bottomScrimView != null) {
                bottomScrimView.Animate()?.Cancel();
                bottomScrimView.Visibility = ViewStates.Gone;
            }
            return;
        }
        int myGen = ++navVisAnimGen;
        navFrostedHost.Animate()?.Cancel();
        bottomScrimView?.Animate()?.Cancel();

        if (!animate) {
            navFrostedHost.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
            navFrostedHost.Alpha = visible ? 1f : 0f;
            navFrostedHost.TranslationY = visible ? 0f : Dp(34);
            navFrostedHost.ScaleX = visible ? 1f : 0.94f;
            navFrostedHost.ScaleY = visible ? 1f : 0.94f;
            if (bottomScrimView != null) {
                bottomScrimView.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
                bottomScrimView.Alpha = visible ? 1f : 0f;
            }
            if (visible) {
                navFrostedHost.Post(() => {
                    PositionTabIndicator(animate: false);
                    navFrostedHost?.RefreshBlur(force: true);
                });
            }
            return;
        }

        if (visible) {
            if (navFrostedHost.Visibility != ViewStates.Visible) {
                navFrostedHost.Visibility = ViewStates.Visible;
                navFrostedHost.Alpha = 0f;
                navFrostedHost.TranslationY = Dp(32);
                navFrostedHost.ScaleX = 0.94f;
                navFrostedHost.ScaleY = 0.94f;
            }
            navFrostedHost.Animate()
                .Alpha(1f)
                .TranslationY(0f)
                .ScaleX(1f)
                .ScaleY(1f)
                .SetDuration(290)
                .SetInterpolator(CreateOneUiEase())
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != navVisAnimGen) return;
                    PositionTabIndicator(animate: false);
                    navFrostedHost?.RefreshBlur(force: true);
                }))
                .Start();
            if (bottomScrimView != null) {
                bottomScrimView.Visibility = ViewStates.Visible;
                bottomScrimView.Animate()
                    .Alpha(1f)
                    .SetDuration(250)
                    .SetInterpolator(CreateOneUiEase())
                    .Start();
            }
        } else {
            navFrostedHost.Animate()
                .Alpha(0f)
                .TranslationY(Dp(34))
                .ScaleX(0.94f)
                .ScaleY(0.94f)
                .SetDuration(220)
                .SetInterpolator(new Android.Views.Animations.AccelerateInterpolator(1.3f))
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != navVisAnimGen) return;
                    navFrostedHost.Visibility = ViewStates.Gone;
                }))
                .Start();
            if (bottomScrimView != null) {
                bottomScrimView.Animate()
                    .Alpha(0f)
                    .SetDuration(200)
                    .WithEndAction(new Java.Lang.Runnable(() => {
                        if (uiDestroyed || myGen != navVisAnimGen) return;
                        bottomScrimView.Visibility = ViewStates.Gone;
                    }))
                    .Start();
            }
        }
    }

    void UpdateTabButtonStyles(bool animate = true) {
        if (tabCells == null || tabCells.Length == 0) {
            return;
        }
        for (int i = 0; i < tabCells.Length; i++) {
            bool active = i == activeTab;
            if (tabIcons != null && i < tabIcons.Length && tabIcons[i] != null) {
                tabIcons[i].IsTabSelected = active;
            }
            if (tabLabels != null && i < tabLabels.Length && tabLabels[i] != null) {
                tabLabels[i].SetTextColor(active ? ColText : Color.Argb(184, 210, 212, 220));
                tabLabels[i].Typeface = active ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontRegular(this);
            }
        }
        PositionTabIndicator(animate);
    }

    void PositionTabIndicator(bool animate) {
        if (tabIndicatorPill == null || tabCells == null || activeTab < 0 || activeTab >= tabCells.Length) {
            return;
        }
        View targetHost = activeTab == 4 && tabCells[4].Parent is View parentCell
            ? parentCell
            : tabCells[activeTab];
        int targetW = targetHost.Width;
        int targetH = targetHost.Height;
        if (targetW <= 0 || targetH <= 0) {
            targetHost.Post(() => PositionTabIndicator(animate: false));
            return;
        }

        float startX = tabIndicatorPill.TranslationX;
        float endX = targetHost.Left;
        int startW = tabIndicatorPill.Width > 0 ? tabIndicatorPill.Width : targetW;

        var lp = tabIndicatorPill.LayoutParameters;
        if (lp != null && lp.Height != targetH) {
            lp.Height = targetH;
            tabIndicatorPill.LayoutParameters = lp;
        }

        tabPillAnimator?.Cancel();
        OneUiMotionBlur.ClearMotionBlur(tabIndicatorPill);
        if (!animate) {
            tabIndicatorPill.TranslationX = endX;
            if (lp != null) {
                lp.Width = targetW;
                tabIndicatorPill.LayoutParameters = lp;
            }
            return;
        }

        float density = Resources?.DisplayMetrics?.Density ?? 3f;
        float prevPillX = startX;
        long prevPillMs = SystemClock.UptimeMillis();

        tabPillAnimator = ValueAnimator.OfFloat(0f, 1f);
        tabPillAnimator.SetDuration(300);
        tabPillAnimator.SetInterpolator(new Android.Views.Animations.PathInterpolator(0.22f, 1f, 0.36f, 1f));
        tabPillAnimator.Update += (_, e) => {
            float f = (float)(e.Animation?.AnimatedValue ?? 1f);
            float curX = startX + (endX - startX) * f;
            tabIndicatorPill.TranslationX = curX;
            var curLp = tabIndicatorPill.LayoutParameters;
            if (curLp != null) {
                curLp.Width = (int)Math.Round(startW + (targetW - startW) * f);
                tabIndicatorPill.LayoutParameters = curLp;
            }
            long nowMs = SystemClock.UptimeMillis();
            float dtSec = Math.Max(0.004f, (nowMs - prevPillMs) * 0.001f);
            float vel = MathF.Abs(curX - prevPillX) / dtSec;
            prevPillX = curX;
            prevPillMs = nowMs;
            float pillBlur = OneUiMotionBlur.ComputeHorizontalBlurRadiusPx(vel * 2.2f, density, maxBlurDp: 10f);
            OneUiMotionBlur.ApplyHorizontalMotionBlur(tabIndicatorPill, pillBlur);
        };
        tabPillAnimator.AnimationEnd += (_, _) => OneUiMotionBlur.ClearMotionBlur(tabIndicatorPill);
        tabPillAnimator.AnimationCancel += (_, _) => OneUiMotionBlur.ClearMotionBlur(tabIndicatorPill);
        tabPillAnimator.Start();
    }

    void RequestMediaPermissionsIfNeeded() {
        List<string> needed = [];
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
            if (CheckSelfPermission(Manifest.Permission.ReadMediaAudio) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadMediaAudio);
            }
            bool hasVisualAccess = CheckSelfPermission(Manifest.Permission.ReadMediaVideo) == Permission.Granted ||
                (OperatingSystem.IsAndroidVersionAtLeast(34) &&
                 CheckSelfPermission(Manifest.Permission.ReadMediaVisualUserSelected) == Permission.Granted);
            if (!hasVisualAccess) {
                needed.Add(Manifest.Permission.ReadMediaVideo);
            }
            if (CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted) {
                needed.Add(Manifest.Permission.PostNotifications);
            }
        } else {
            if (CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadExternalStorage);
            }
            if (!OperatingSystem.IsAndroidVersionAtLeast(30) &&
                CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted) {
                needed.Add(Manifest.Permission.WriteExternalStorage);
            }
        }
        if (needed.Count > 0) {
            RequestPermissions(needed.ToArray(), ReqPermissions);
        }
    }

    void RequestBluetoothPermissionsIfNeeded(Cavernize.Logic.Spatial.HeadTrackerSourceKind source) {
        List<string> needed = [];
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            if (CheckSelfPermission(Manifest.Permission.BluetoothConnect) != Permission.Granted) {
                needed.Add(Manifest.Permission.BluetoothConnect);
            }
            if (source == Cavernize.Logic.Spatial.HeadTrackerSourceKind.BleOsc &&
                CheckSelfPermission(Manifest.Permission.BluetoothScan) != Permission.Granted) {
                needed.Add(Manifest.Permission.BluetoothScan);
            }
        } else if (source == Cavernize.Logic.Spatial.HeadTrackerSourceKind.BleOsc &&
                   CheckSelfPermission(Manifest.Permission.AccessFineLocation) != Permission.Granted) {
            needed.Add(Manifest.Permission.AccessFineLocation);
        }
        if (needed.Count > 0) {
            RequestPermissions(needed.ToArray(), ReqBluetoothPermissions);
        }
    }

    bool RequestRecordAudioPermissionIfNeeded() {
        if (CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted) {
            return true;
        }
        RequestPermissions([Manifest.Permission.RecordAudio], ReqRecordAudioPermission);
        return false;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == ReqBluetoothPermissions) {
            bool allGranted = grantResults != null && grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted);
            if (!allGranted) {
                ShowToast(S["HeadTrackingPermNeededToast"]);
            }
            headTrackingManager?.ApplySettings();
            RefreshHeadTrackingUi();
        } else if (requestCode == ReqRecordAudioPermission) {
            bool granted = grantResults != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            if (!granted) {
                ShowToast(S["ProAudioMicPermNeededToast"]);
            }
            audioOutput?.ApplyProAudioConfig(store.BuildProAudioConfig());
            RefreshProAudioUi();
        }
    }

    void HandleIncomingIntent(Intent intent) {
        Android.Net.Uri uri = intent.Data;
        if (uri == null) {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
                uri = intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri;
            } else if (intent.Extras != null) {
#pragma warning disable CS0618, CA1422
                uri = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
#pragma warning restore CS0618, CA1422
            }
        }
        if (uri != null) {
            LoadUriAsync(uri);
        }
    }

    void LaunchFilePicker(int requestCode, bool allowMultiple) {
        Intent intent = new(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, allowMultiple);
        StartActivityForResult(intent, requestCode);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data) {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data == null) {
            return;
        }

        if (requestCode == ReqOpenMedia && data.Data != null) {
            LoadUriAsync(data.Data);
        } else if (requestCode == ReqBatchMedia) {
            List<Android.Net.Uri> uris = [];
            if (data.ClipData != null) {
                for (int i = 0; i < data.ClipData.ItemCount; i++) {
                    uris.Add(data.ClipData.GetItemAt(i).Uri);
                }
            } else if (data.Data != null) {
                uris.Add(data.Data);
            }
            if (uris.Count > 0) {
                AddUrisToQueueAsync(uris);
            }
        } else if (requestCode == ReqLoadHrir && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => ImportCustomBinauralFile(path));
            });
        } else if (requestCode == ReqLoadFilters && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => TryLoadRoomFilters(path));
            });
        } else if (requestCode == ReqLoadSubtitles && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => LoadExternalSubtitleFromPath(path));
            });
        } else if (requestCode == ReqLoadAutoEq && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => ImportCustomAutoEqFile(path));
            });
        } else if (requestCode == ReqLoadOnnxModel && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => ImportCustomOnnxModelFromPath(path));
            });
        }
    }

    void LoadExternalSubtitleFromPath(string path) {
        if (string.IsNullOrEmpty(path)) return;
        Task.Run(() => {
            List<SubtitleCue> cues = SubtitleService.LoadFromFilePath(path);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (cues.Count == 0) {
                    ShowError(S["SubtitlesNoneFound"]);
                    return;
                }
                SubtitleTrackInfo imported = new() {
                    Id = "ext:" + path,
                    Title = Path.GetFileName(path),
                    ExternalFilePath = path
                };
                discoveredSubtitles.RemoveAll(s => string.Equals(s.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
                discoveredSubtitles.Insert(0, imported);
                activeSubtitleTrackId = imported.Id;
                activeSubtitleCues = cues;
                musicVideoStage?.SetSubtitles(activeSubtitleCues);
                RefreshBottomActionBarVisuals();
            });
        });
    }


    /// <summary>
    /// Tab container supporting horizontal swipe navigation between tabs when outside the Play tab
    /// (allows swiping from 3D Room into Play, while disabling swipe away from Play), with strict touch
    /// disambiguation so 3D Room orbit, sliders, and scrollable chips are never intercepted.
    /// </summary>
    sealed class TabSwipeHostLayout : FrameLayout {
        readonly Func<int> getActiveTab;
        readonly Func<int> getTabCount;
        readonly Func<int, View> getContainerAt;
        readonly Action<int, float> switchTabFromDrag;
        readonly float touchSlop;
        readonly float commitDistPx;
        readonly float flingVelPxPerSec;
        readonly float density;

        float downRawX;
        float downRawY;
        float lastMoveRawX;
        long lastMoveTimeMs;
        long downTimeMs;
        float liveVelPxPerSec;
        bool ignoreGesture;
        bool isSwiping;
        int peekingNeighborTab = -1;

        public TabSwipeHostLayout(
            Context ctx,
            Func<int> getActiveTab,
            Func<int> getTabCount,
            Func<int, View> getContainerAt,
            Action<int, float> switchTabFromDrag) : base(ctx) {
            this.getActiveTab = getActiveTab;
            this.getTabCount = getTabCount;
            this.getContainerAt = getContainerAt;
            this.switchTabFromDrag = switchTabFromDrag;
            density = ctx.Resources?.DisplayMetrics?.Density ?? 3f;
            touchSlop = ViewConfiguration.Get(ctx)?.ScaledTouchSlop ?? (int)(8f * density);
            commitDistPx = 64f * density;
            flingVelPxPerSec = 360f * density;
        }

        public override bool OnInterceptTouchEvent(MotionEvent ev) {
            if (ev == null) {
                return false;
            }
            int curTab = getActiveTab();
            // Never allow horizontal swipe away from Play (tab 0)
            if (curTab <= 0) {
                isSwiping = false;
                ignoreGesture = true;
                return false;
            }

            switch (ev.ActionMasked) {
                case MotionEventActions.Down:
                    downRawX = ev.RawX;
                    downRawY = ev.RawY;
                    lastMoveRawX = downRawX;
                    downTimeMs = SystemClock.UptimeMillis();
                    lastMoveTimeMs = downTimeMs;
                    liveVelPxPerSec = 0f;
                    isSwiping = false;
                    peekingNeighborTab = -1;
                    ignoreGesture = IsTouchInsideHorizontalControl(getContainerAt(curTab), downRawX, downRawY);
                    return false;

                case MotionEventActions.Move:
                    if (ignoreGesture) {
                        return false;
                    }
                    float dx = ev.RawX - downRawX;
                    float dy = ev.RawY - downRawY;
                    if (MathF.Abs(dy) > touchSlop * 1.25f && MathF.Abs(dy) > MathF.Abs(dx)) {
                        ignoreGesture = true;
                        return false;
                    }
                    if (MathF.Abs(dx) > touchSlop * 1.45f && MathF.Abs(dx) > MathF.Abs(dy) * 1.65f) {
                        int count = getTabCount();
                        bool canGoLeft = dx > 0 && curTab > 0;
                        bool canGoRight = dx < 0 && curTab < count - 1;
                        if (canGoLeft || canGoRight) {
                            isSwiping = true;
                            lastMoveRawX = ev.RawX;
                            lastMoveTimeMs = SystemClock.UptimeMillis();
                            return true;
                        }
                    }
                    return false;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    isSwiping = false;
                    ignoreGesture = false;
                    return false;
            }
            return false;
        }

        public override bool OnTouchEvent(MotionEvent ev) {
            if (ev == null || !isSwiping) {
                return base.OnTouchEvent(ev);
            }
            int curTab = getActiveTab();
            int count = getTabCount();
            View activeView = getContainerAt(curTab);
            float viewportW = Math.Max(1f, Width);
            float dx = ev.RawX - downRawX;

            switch (ev.ActionMasked) {
                case MotionEventActions.Move: {
                    long nowMs = SystemClock.UptimeMillis();
                    float dtSec = Math.Max(0.004f, (nowMs - lastMoveTimeMs) * 0.001f);
                    float instVel = MathF.Abs(ev.RawX - lastMoveRawX) / dtSec;
                    liveVelPxPerSec = liveVelPxPerSec * 0.45f + instVel * 0.55f;
                    lastMoveRawX = ev.RawX;
                    lastMoveTimeMs = nowMs;

                    bool allowedLeft = dx > 0 && curTab > 0;
                    bool allowedRight = dx < 0 && curTab < count - 1;
                    float offsetX = (allowedLeft || allowedRight)
                        ? Math.Clamp(dx, -viewportW, viewportW)
                        : dx * 0.14f;

                    int desiredNeighbor = allowedLeft ? curTab - 1 : (allowedRight ? curTab + 1 : -1);
                    if (peekingNeighborTab >= 0 && peekingNeighborTab != desiredNeighbor) {
                        View oldN = getContainerAt(peekingNeighborTab);
                        if (oldN != null) {
                            OneUiMotionBlur.ClearMotionBlur(oldN);
                            oldN.TranslationX = 0f;
                            oldN.Visibility = ViewStates.Gone;
                        }
                    }
                    peekingNeighborTab = desiredNeighbor;

                    float blurPx = OneUiMotionBlur.ComputeHorizontalBlurRadiusPx(liveVelPxPerSec, density, maxBlurDp: 20f);
                    if (activeView != null) {
                        activeView.TranslationX = offsetX;
                        OneUiMotionBlur.ApplyHorizontalMotionBlur(activeView, blurPx);
                    }
                    if (desiredNeighbor >= 0) {
                        View neighbor = getContainerAt(desiredNeighbor);
                        if (neighbor != null) {
                            neighbor.Visibility = ViewStates.Visible;
                            neighbor.Alpha = 1f;
                            neighbor.ScaleX = 1f;
                            neighbor.ScaleY = 1f;
                            neighbor.TranslationX = offsetX + (desiredNeighbor - curTab) * viewportW;
                            OneUiMotionBlur.ApplyHorizontalMotionBlur(neighbor, blurPx);
                        }
                    }
                    return true;
                }

                case MotionEventActions.Up:
                case MotionEventActions.Cancel: {
                    isSwiping = false;
                    long dt = Math.Max(16, SystemClock.UptimeMillis() - downTimeMs);
                    float avgVel = MathF.Abs(dx) * 1000f / dt;
                    float effectiveVel = Math.Max(avgVel, liveVelPxPerSec);
                    bool commit = ev.ActionMasked == MotionEventActions.Up &&
                                  (MathF.Abs(dx) >= commitDistPx || (MathF.Abs(dx) >= touchSlop * 2f && effectiveVel >= flingVelPxPerSec));
                    int target = curTab;
                    if (commit) {
                        if (dx > 0 && curTab > 0) {
                            target = curTab - 1;
                        } else if (dx < 0 && curTab < count - 1) {
                            target = curTab + 1;
                        }
                    }
                    int neighborIdx = peekingNeighborTab;
                    peekingNeighborTab = -1;
                    float currentOffset = activeView?.TranslationX ?? 0f;
                    if (target != curTab) {
                        switchTabFromDrag(target, currentOffset);
                    } else {
                        View neighbor = neighborIdx >= 0 ? getContainerAt(neighborIdx) : null;
                        ValueAnimator snap = ValueAnimator.OfFloat(currentOffset, 0f);
                        snap.SetDuration(220);
                        snap.SetInterpolator(new Android.Views.Animations.PathInterpolator(0.22f, 1f, 0.36f, 1f));
                        float pOff = currentOffset;
                        long pMs = SystemClock.UptimeMillis();
                        snap.Update += (_, e) => {
                            float cur = (float)(e.Animation?.AnimatedValue ?? 0f);
                            long nMs = SystemClock.UptimeMillis();
                            float vel = MathF.Abs(cur - pOff) / Math.Max(0.004f, (nMs - pMs) * 0.001f);
                            pOff = cur;
                            pMs = nMs;
                            float b = OneUiMotionBlur.ComputeHorizontalBlurRadiusPx(vel, density, maxBlurDp: 16f);
                            if (activeView != null) {
                                activeView.TranslationX = cur;
                                OneUiMotionBlur.ApplyHorizontalMotionBlur(activeView, b);
                            }
                            if (neighbor != null) {
                                neighbor.TranslationX = cur + (neighborIdx - curTab) * viewportW;
                                OneUiMotionBlur.ApplyHorizontalMotionBlur(neighbor, b);
                            }
                        };
                        void CleanSnap() {
                            if (activeView != null) {
                                OneUiMotionBlur.ClearMotionBlur(activeView);
                                activeView.TranslationX = 0f;
                            }
                            if (neighbor != null) {
                                OneUiMotionBlur.ClearMotionBlur(neighbor);
                                neighbor.TranslationX = 0f;
                                neighbor.Visibility = ViewStates.Gone;
                            }
                        }
                        snap.AnimationEnd += (_, _) => CleanSnap();
                        snap.AnimationCancel += (_, _) => CleanSnap();
                        snap.Start();
                    }
                    return true;
                }
            }
            return true;
        }

        static bool IsTouchInsideHorizontalControl(View root, float rawX, float rawY) {
            if (root == null || root.Visibility != ViewStates.Visible) {
                return false;
            }
            int[] loc = new int[2];
            root.GetLocationOnScreen(loc);
            if (rawX < loc[0] || rawX > loc[0] + root.Width || rawY < loc[1] || rawY > loc[1] + root.Height) {
                return false;
            }
            if (root is RoomSimulatorView or OneUiSeekBar or OneUiWaveMediaSeekBar or EditText) {
                return true;
            }
            if (root is HorizontalScrollView hsv && (hsv.CanScrollHorizontally(-1) || hsv.CanScrollHorizontally(1))) {
                return true;
            }
            if (root is ViewGroup vg) {
                for (int i = vg.ChildCount - 1; i >= 0; i--) {
                    if (IsTouchInsideHorizontalControl(vg.GetChildAt(i), rawX, rawY)) {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
