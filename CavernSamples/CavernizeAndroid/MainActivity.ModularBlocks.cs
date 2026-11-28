using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

using Cavernize.Logic.Models;

using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

/// <summary>
/// W55.5.2 (One UI 9 QsEditMode): True in-place long-press-border drag-reorder for modular control blocks
/// on Now Playing and 3D Stage tabs. Uses verified SystemUI.apk <c>EditModeTileDefaults</c> /
/// <c>SecQSImplAnimatorBase</c> physics (lift scale 1.035, 10dp elevation, 200ms settle, 28dp border,
/// zero full-screen RenderEffect blur, zero legacy ↑/↓ dialog buttons).
/// </summary>
public partial class MainActivity {
    LinearLayout modularPlayContainer;
    readonly Dictionary<string, View> modularPlayBlocks = new(StringComparer.Ordinal);
    ModularQuickPanelLayout playModularLayout;

    LinearLayout modularRoomContainer;
    readonly Dictionary<string, View> modularRoomBlocks = new(StringComparer.Ordinal);
    ModularQuickPanelLayout roomModularLayout;

    bool isPlayModularEditMode;
    bool isRoomModularEditMode;
    LinearLayout playModularEditToolbar;
    LinearLayout roomModularEditToolbar;
    readonly Dictionary<View, Drawable> modularSavedBackgrounds = new();

    void RegisterPlayModularBlocks(LinearLayout container) {
        modularPlayContainer = container;
        modularPlayBlocks.Clear();
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.Stage, mediaStageFrame);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.Title, musicTitleRow);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.SeekBar, musicWaveSeekBar);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.Badges, badgeFormatRow);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.Transport, musicTransportRow);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.Volume, musicVolRow);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.SpatialPills, musicSpatialToggleRow);
        RegisterBlockIfNotNull(modularPlayBlocks, ModularQuickPanelLayout.PlayIds.BottomActions, musicBottomActionRow);

        playModularLayout = ModularQuickPanelLayout.FromJsonOrDefault(
            store?.ModularPlayLayoutJson,
            ModularQuickPanelLayout.CreateDefaultPlayLayout());
        AttachModularLongPressTriggers(modularPlayBlocks, isRoomTab: false);
        ApplyPlayModularLayout();
    }

    void RegisterRoomModularBlocks(
        LinearLayout container,
        View signalChainBar,
        View roomCard,
        View miniTransportStrip,
        View xrayCard,
        View quickCard,
        View mixerCard) {
        modularRoomContainer = container;
        modularRoomBlocks.Clear();
        RegisterBlockIfNotNull(modularRoomBlocks, ModularQuickPanelLayout.RoomIds.SignalChain, signalChainBar);
        RegisterBlockIfNotNull(modularRoomBlocks, ModularQuickPanelLayout.RoomIds.RoomCanvas, roomCard);
        RegisterBlockIfNotNull(modularRoomBlocks, ModularQuickPanelLayout.RoomIds.MiniTransport, miniTransportStrip);
        RegisterBlockIfNotNull(modularRoomBlocks, ModularQuickPanelLayout.RoomIds.XRayInspector, xrayCard);
        RegisterBlockIfNotNull(modularRoomBlocks, ModularQuickPanelLayout.RoomIds.QuickPanel, quickCard);
        RegisterBlockIfNotNull(modularRoomBlocks, ModularQuickPanelLayout.RoomIds.ChannelMixer, mixerCard);

        roomModularLayout = ModularQuickPanelLayout.FromJsonOrDefault(
            store?.ModularRoomLayoutJson,
            ModularQuickPanelLayout.CreateDefaultRoomLayout());
        AttachModularLongPressTriggers(modularRoomBlocks, isRoomTab: true);
        ApplyRoomModularLayout();
    }

    void AttachModularLongPressTriggers(Dictionary<string, View> registry, bool isRoomTab) {
        foreach (KeyValuePair<string, View> kv in registry) {
            View blockView = kv.Value;
            string blockId = kv.Key;
            if (blockView == null) {
                continue;
            }
            blockView.LongClickable = true;
            blockView.LongClick += (_, ev) => {
                if (isRoomTab ? !isRoomModularEditMode : !isPlayModularEditMode) {
                    blockView.PerformHapticFeedback(FeedbackConstants.LongPress);
                    EnterModularInlineEditMode(isRoomTab);
                    ev.Handled = true;
                }
            };
            blockView.SetOnTouchListener(new ModularBlockDragTouchListener(this, blockView, blockId, isRoomTab));
        }
    }

    static void RegisterBlockIfNotNull(Dictionary<string, View> map, string id, View view) {
        if (view != null) {
            map[id] = view;
        }
    }

    bool IsPlayModularBlockVisible(string id) => playModularLayout?.IsVisible(id) ?? true;

    void ApplyPlayModularLayout() {
        if (modularPlayContainer == null || playModularLayout == null) {
            return;
        }
        ReorderLinearLayoutInPlace(modularPlayContainer, modularPlayBlocks, playModularLayout, isPlayTab: true);
        UpdatePlayTabSpacingForViewport(force: true);
    }

    void ApplyRoomModularLayout() {
        if (modularRoomContainer == null || roomModularLayout == null) {
            return;
        }
        ReorderLinearLayoutInPlace(modularRoomContainer, modularRoomBlocks, roomModularLayout, isPlayTab: false);
    }

    void ReorderLinearLayoutInPlace(
        LinearLayout container,
        Dictionary<string, View> registry,
        ModularQuickPanelLayout layout,
        bool isPlayTab) {
        if (container == null || layout?.Blocks == null || layout.Blocks.Count == 0) {
            return;
        }

        bool isLyricsMode = isPlayTab && musicStageMode == PlayerStageMode.Lyrics;
        bool inEditMode = isPlayTab ? isPlayModularEditMode : isRoomModularEditMode;
        LinearLayout editToolbar = isPlayTab ? playModularEditToolbar : roomModularEditToolbar;

        List<View> desiredOrder = new(layout.Blocks.Count + 1);
        if (inEditMode && editToolbar != null) {
            desiredOrder.Add(editToolbar);
        }
        for (int i = 0; i < layout.Blocks.Count; i++) {
            ModularBlockEntry entry = layout.Blocks[i];
            if (!registry.TryGetValue(entry.Id, out View v) || v == null) {
                continue;
            }
            desiredOrder.Add(v);
            bool hiddenByLyrics = isLyricsMode &&
                (entry.Id == ModularQuickPanelLayout.PlayIds.Title ||
                 entry.Id == ModularQuickPanelLayout.PlayIds.Badges ||
                 entry.Id == ModularQuickPanelLayout.PlayIds.Volume ||
                 entry.Id == ModularQuickPanelLayout.PlayIds.SpatialPills);
            ViewStates targetVis = (!hiddenByLyrics && (entry.Locked || entry.Visible))
                ? ViewStates.Visible
                : ViewStates.Gone;
            if (v.Visibility != targetVis) {
                v.Visibility = targetVis;
                v.Alpha = 1f;
            }
        }

        bool orderMatches = container.ChildCount == desiredOrder.Count;
        if (orderMatches) {
            for (int i = 0; i < desiredOrder.Count; i++) {
                if (!ReferenceEquals(container.GetChildAt(i), desiredOrder[i])) {
                    orderMatches = false;
                    break;
                }
            }
        }
        if (orderMatches) {
            return;
        }

        List<(View View, ViewGroup.LayoutParams Params)> preserved = new(desiredOrder.Count);
        for (int i = 0; i < desiredOrder.Count; i++) {
            View v = desiredOrder[i];
            preserved.Add((v, v.LayoutParameters));
        }
        for (int i = 0; i < preserved.Count; i++) {
            if (preserved[i].View.Parent is ViewGroup parent) {
                parent.RemoveView(preserved[i].View);
            }
        }
        for (int i = 0; i < preserved.Count; i++) {
            if (preserved[i].Params != null) {
                container.AddView(preserved[i].View, preserved[i].Params);
            } else {
                container.AddView(preserved[i].View);
            }
        }
    }

    /// <summary>
    /// Enters authentic One UI 9 <c>QsEditMode</c> in-place direct drag-reorder on Play or 3D Stage blocks.
    /// </summary>
    void EnterModularInlineEditMode(bool isRoomTab) {
        LinearLayout container = isRoomTab ? modularRoomContainer : modularPlayContainer;
        Dictionary<string, View> registry = isRoomTab ? modularRoomBlocks : modularPlayBlocks;
        ModularQuickPanelLayout layout = isRoomTab ? roomModularLayout : playModularLayout;
        if (container == null || layout == null) {
            return;
        }

        if (isRoomTab) {
            if (isRoomModularEditMode) {
                return;
            }
            isRoomModularEditMode = true;
            roomModularEditToolbar ??= BuildInlineModularEditToolbar(isRoomTab: true);
        } else {
            if (isPlayModularEditMode) {
                return;
            }
            isPlayModularEditMode = true;
            playModularEditToolbar ??= BuildInlineModularEditToolbar(isRoomTab: false);
        }

        foreach (KeyValuePair<string, View> kv in registry) {
            View v = kv.Value;
            if (v == null) {
                continue;
            }
            if (!modularSavedBackgrounds.ContainsKey(v)) {
                modularSavedBackgrounds[v] = v.Background;
            }
            v.Background = OneUiTheme.CreateRoundedDrawable(
                this,
                Color.Argb(54, 46, 48, 60),
                OneUiTheme.QsEditCornerDp,
                OneUiTheme.QsEditBorderStroke,
                1.5f);
            v.Animate()
                .ScaleX(0.97f)
                .ScaleY(0.97f)
                .SetDuration(OneUiTheme.QsEditSettleMs)
                .SetInterpolator(new PathInterpolator(0.22f, 1f, 0.36f, 1f))
                .Start();
        }

        if (isRoomTab) {
            ApplyRoomModularLayout();
        } else {
            ApplyPlayModularLayout();
        }
        ShowToast(S["ModLayoutDragActiveHint"]);
    }

    /// <summary>
    /// Exits in-place <c>QsEditMode</c>, restores resting scale/borders, and persists the updated layout.
    /// </summary>
    void ExitModularInlineEditMode(bool isRoomTab) {
        Dictionary<string, View> registry = isRoomTab ? modularRoomBlocks : modularPlayBlocks;
        if (isRoomTab) {
            if (!isRoomModularEditMode) {
                return;
            }
            isRoomModularEditMode = false;
        } else {
            if (!isPlayModularEditMode) {
                return;
            }
            isPlayModularEditMode = false;
        }

        foreach (KeyValuePair<string, View> kv in registry) {
            View v = kv.Value;
            if (v == null) {
                continue;
            }
            if (modularSavedBackgrounds.TryGetValue(v, out Drawable origBg)) {
                v.Background = origBg;
                modularSavedBackgrounds.Remove(v);
            }
            v.Elevation = 0f;
            v.Animate()
                .TranslationY(0f)
                .ScaleX(1f)
                .ScaleY(1f)
                .Alpha(1f)
                .SetDuration(OneUiTheme.QsEditSettleMs)
                .SetInterpolator(new PathInterpolator(0.22f, 1f, 0.36f, 1f))
                .Start();
        }

        PersistModularLayout(isRoomTab);
        if (isRoomTab) {
            ApplyRoomModularLayout();
        } else {
            ApplyPlayModularLayout();
        }
    }

    LinearLayout BuildInlineModularEditToolbar(bool isRoomTab) {
        LinearLayout bar = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false,
            Background = OneUiTheme.CreateRoundedDrawable(
                this,
                OneUiTheme.SecQsDetailBg,
                OneUiTheme.PillCornerDp,
                OneUiTheme.SecQsDetailStroke,
                1f)
        };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(12), Dp(6), Dp(8), Dp(6));
        bar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        };

        TextView hint = new(this) {
            Text = S["ModLayoutDragToolbarHint"],
            TextSize = 11.8f
        };
        hint.SetTextColor(OneUiTheme.TextPrimary);
        hint.Typeface = OneUiTheme.GetAppleFontSemibold(this);
        hint.SetSingleLine(true);
        hint.Ellipsize = TextUtils.TruncateAt.End;
        bar.AddView(hint, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
            RightMargin = Dp(6)
        });

        Button resetBtn = PillButton(S["ModLayoutResetBtn"], () => {
            ResetModularLayoutToDefault(isRoomTab);
            ShowToast(S["ModLayoutResetToast"]);
        });
        resetBtn.TextSize = 11.5f;
        resetBtn.SetMinimumHeight(Dp(34));
        resetBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        bar.AddView(resetBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            RightMargin = Dp(6)
        });

        Button doneBtn = PrimaryButton(S["ModLayoutDoneBtn"], () => ExitModularInlineEditMode(isRoomTab));
        doneBtn.TextSize = 11.5f;
        doneBtn.SetMinimumHeight(Dp(34));
        doneBtn.SetPadding(Dp(12), Dp(4), Dp(12), Dp(4));
        bar.AddView(doneBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return bar;
    }

    void ResetModularLayoutToDefault(bool isRoomTab) {
        if (isRoomTab) {
            roomModularLayout = ModularQuickPanelLayout.CreateDefaultRoomLayout();
            PersistModularLayout(isRoomTab: true);
            ApplyRoomModularLayout();
        } else {
            playModularLayout = ModularQuickPanelLayout.CreateDefaultPlayLayout();
            PersistModularLayout(isRoomTab: false);
            ApplyPlayModularLayout();
        }
    }

    void PersistModularLayout(bool isRoomTab) {
        if (store == null) {
            return;
        }
        if (isRoomTab && roomModularLayout != null) {
            store.ModularRoomLayoutJson = roomModularLayout.ToJson();
        } else if (!isRoomTab && playModularLayout != null) {
            store.ModularPlayLayoutJson = playModularLayout.ToJson();
        }
    }

    void SwapModularBlockByDrag(bool isRoomTab, string draggedId, int directionDelta) {
        ModularQuickPanelLayout layout = isRoomTab ? roomModularLayout : playModularLayout;
        if (layout == null || directionDelta == 0) {
            return;
        }
        bool moved = directionDelta < 0 ? layout.MoveUp(draggedId) : layout.MoveDown(draggedId);
        if (moved) {
            PersistModularLayout(isRoomTab);
            if (isRoomTab) {
                ApplyRoomModularLayout();
            } else {
                ApplyPlayModularLayout();
            }
        }
    }

    sealed class ModularBlockDragTouchListener(
        MainActivity owner,
        View blockView,
        string blockId,
        bool isRoomTab) : Java.Lang.Object, View.IOnTouchListener {
        float lastRawY;
        float accumDy;
        bool dragging;

        public bool OnTouch(View v, MotionEvent e) {
            bool editActive = isRoomTab ? owner.isRoomModularEditMode : owner.isPlayModularEditMode;
            if (!editActive || e == null) {
                return false;
            }
            switch (e.ActionMasked) {
                case MotionEventActions.Down:
                    lastRawY = e.RawY;
                    accumDy = 0f;
                    dragging = true;
                    v.Parent?.RequestDisallowInterceptTouchEvent(true);
                    v.PerformHapticFeedback(FeedbackConstants.LongPress);
                    v.Elevation = owner.Dp((int)OneUiTheme.QsEditLiftElevationDp);
                    v.Animate()
                        .ScaleX(OneUiTheme.QsEditLiftScale)
                        .ScaleY(OneUiTheme.QsEditLiftScale)
                        .Alpha(0.94f)
                        .SetDuration(120)
                        .Start();
                    return true;

                case MotionEventActions.Move:
                    if (!dragging) {
                        return false;
                    }
                    float stepY = e.RawY - lastRawY;
                    lastRawY = e.RawY;
                    accumDy += stepY;
                    v.TranslationY = Math.Clamp(accumDy, -owner.Dp(72), owner.Dp(72));

                    float swapThreshold = Math.Max(owner.Dp(48), blockView.Height * 0.45f);
                    if (accumDy > swapThreshold) {
                        accumDy = 0f;
                        v.TranslationY = 0f;
                        v.PerformHapticFeedback(FeedbackConstants.ClockTick);
                        owner.SwapModularBlockByDrag(isRoomTab, blockId, +1);
                    } else if (accumDy < -swapThreshold) {
                        accumDy = 0f;
                        v.TranslationY = 0f;
                        v.PerformHapticFeedback(FeedbackConstants.ClockTick);
                        owner.SwapModularBlockByDrag(isRoomTab, blockId, -1);
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (!dragging) {
                        return false;
                    }
                    dragging = false;
                    v.Parent?.RequestDisallowInterceptTouchEvent(false);
                    v.Elevation = 0f;
                    v.Animate()
                        .TranslationY(0f)
                        .ScaleX(0.97f)
                        .ScaleY(0.97f)
                        .Alpha(1f)
                        .SetDuration(OneUiTheme.QsEditSettleMs)
                        .SetInterpolator(new PathInterpolator(0.22f, 1f, 0.36f, 1f))
                        .Start();
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Lightweight Settings card explaining direct long-press-border drag-reorder with 1-tap reset (zero legacy ↑/↓ dialog).
    /// </summary>
    LinearLayout BuildSettingsModularLayoutCard() {
        LinearLayout card = Card(S["ModLayoutCardTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        TextView desc = new(this) { Text = S["ModLayoutDragSettingsDesc"], TextSize = 12.2f };
        desc.SetTextColor(OneUiTheme.TextSub);
        desc.SetPadding(0, 0, 0, Dp(10));
        card.AddView(desc);

        LinearLayout btnRow = HRow();
        Button resetPlayBtn = PillButton(S["ModLayoutResetPlayBtn"], () => {
            ResetModularLayoutToDefault(isRoomTab: false);
            ShowToast(S["ModLayoutResetToast"]);
        });
        resetPlayBtn.SetMinimumHeight(Dp(44));
        Button resetRoomBtn = PillButton(S["ModLayoutResetRoomBtn"], () => {
            ResetModularLayoutToDefault(isRoomTab: true);
            ShowToast(S["ModLayoutResetToast"]);
        });
        resetRoomBtn.SetMinimumHeight(Dp(44));
        btnRow.AddView(resetPlayBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        btnRow.AddView(resetRoomBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(btnRow);
        return card;
    }
}
