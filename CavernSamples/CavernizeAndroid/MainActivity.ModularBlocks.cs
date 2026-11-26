using Android.App;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;

using Cavernize.Logic.Models;

using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

/// <summary>
/// Wave D (W53.4 / B10): One UI 9 modular editable quick-panel control blocks for Play &amp; 3D Room tabs.
/// Reorders and toggles existing Views in-place at 120 Hz zero-jank with zero full-screen RenderEffect blur,
/// WCAG AA contrast (OneUiTheme.OnPrimary), and &gt;=44dp touch targets.
/// </summary>
public partial class MainActivity {
    LinearLayout modularPlayContainer;
    readonly Dictionary<string, View> modularPlayBlocks = new(StringComparer.Ordinal);
    ModularQuickPanelLayout playModularLayout;

    LinearLayout modularRoomContainer;
    readonly Dictionary<string, View> modularRoomBlocks = new(StringComparer.Ordinal);
    ModularQuickPanelLayout roomModularLayout;

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
        ApplyRoomModularLayout();
    }

    static void RegisterBlockIfNotNull(Dictionary<string, View> map, string id, View view) {
        if (view != null) {
            map[id] = view;
        }
    }

    bool IsPlayModularBlockVisible(string id) {
        if (playModularLayout == null) {
            return true;
        }
        return playModularLayout.IsVisible(id);
    }

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

        // Check if child order already matches to avoid unnecessary RemoveView/AddView layout passes at 120 Hz
        List<View> desiredOrder = new(layout.Blocks.Count);
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

        // Preserve exact LayoutParameters while re-attaching existing View instances cleanly (zero leak, zero recreate)
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

    string ResolveModularBlockLabel(string id) => id switch {
        ModularQuickPanelLayout.PlayIds.Stage => S["ModBlockPlayStage"],
        ModularQuickPanelLayout.PlayIds.Title => S["ModBlockPlayTitle"],
        ModularQuickPanelLayout.PlayIds.SeekBar => S["ModBlockPlaySeek"],
        ModularQuickPanelLayout.PlayIds.Badges => S["ModBlockPlayBadges"],
        ModularQuickPanelLayout.PlayIds.Transport => S["ModBlockPlayTransport"],
        ModularQuickPanelLayout.PlayIds.Volume => S["ModBlockPlayVolume"],
        ModularQuickPanelLayout.PlayIds.SpatialPills => S["ModBlockPlaySpatialPills"],
        ModularQuickPanelLayout.PlayIds.BottomActions => S["ModBlockPlayBottomActions"],
        ModularQuickPanelLayout.RoomIds.SignalChain => S["ModBlockRoomSignalChain"],
        ModularQuickPanelLayout.RoomIds.RoomCanvas => S["ModBlockRoomCanvas"],
        ModularQuickPanelLayout.RoomIds.MiniTransport => S["ModBlockRoomMiniTransport"],
        ModularQuickPanelLayout.RoomIds.XRayInspector => S["ModBlockRoomXRay"],
        ModularQuickPanelLayout.RoomIds.QuickPanel => S["ModBlockRoomQuickPanel"],
        ModularQuickPanelLayout.RoomIds.ChannelMixer => S["ModBlockRoomMixer"],
        _ => id
    };

    /// <summary>
    /// Opens the One UI 9 Modular Quick-Panel Layout Editor dialog for Play or 3D Room,
    /// allowing instant live block reordering, visibility toggles, and 1-tap default reset.
    /// </summary>
    void ShowModularLayoutEditorDialog(bool isRoomTab) {
        if (uiDestroyed || IsFinishing) {
            return;
        }
        ModularQuickPanelLayout defaults = isRoomTab
            ? ModularQuickPanelLayout.CreateDefaultRoomLayout()
            : ModularQuickPanelLayout.CreateDefaultPlayLayout();
        ModularQuickPanelLayout active = isRoomTab
            ? (roomModularLayout ??= ModularQuickPanelLayout.FromJsonOrDefault(store?.ModularRoomLayoutJson, defaults))
            : (playModularLayout ??= ModularQuickPanelLayout.FromJsonOrDefault(store?.ModularPlayLayoutJson, defaults));

        AlertDialog dialog = null;

        LinearLayout root = new(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(18), Dp(18), Dp(18), Dp(14));
        root.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            OneUiTheme.SurfaceModal,
            26f,
            OneUiTheme.BorderSubtle,
            1f);

        // Header
        LinearLayout header = HRow();
        header.BaselineAligned = false;
        header.SetGravity(GravityFlags.CenterVertical);
        header.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sliders, OneUiTheme.IconSpatial, 32));
        TextView titleTv = SectionTitle(isRoomTab ? S["ModLayoutRoomTitle"] : S["ModLayoutPlayTitle"]);
        titleTv.SetPadding(Dp(10), 0, 0, 0);
        header.AddView(titleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(4)
        });

        TextView subTv = Label(S["ModLayoutSubtitle"], 12f, OneUiTheme.TextSub);
        root.AddView(subTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(12)
        });

        LinearLayout listHost = new(this) { Orientation = Orientation.Vertical };
        ScrollView scroll = new(this) { VerticalScrollBarEnabled = false };
        scroll.AddView(listHost, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f) {
            BottomMargin = Dp(12)
        });

        void PersistAndApply() {
            if (store != null) {
                if (isRoomTab) {
                    store.ModularRoomLayoutJson = active.ToJson();
                } else {
                    store.ModularPlayLayoutJson = active.ToJson();
                }
            }
            if (isRoomTab) {
                ApplyRoomModularLayout();
            } else {
                ApplyPlayModularLayout();
            }
        }

        void RebuildRows() {
            listHost.RemoveAllViews();
            for (int i = 0; i < active.Blocks.Count; i++) {
                ModularBlockEntry entry = active.Blocks[i];
                string blockId = entry.Id;

                LinearLayout row = HRow();
                row.BaselineAligned = false;
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(12), Dp(8), Dp(10), Dp(8));
                row.SetMinimumHeight(Dp(52));
                row.Background = OneUiTheme.CreateRoundedDrawable(
                    this,
                    OneUiTheme.SurfaceModalRow,
                    16f,
                    OneUiTheme.BorderSubtle,
                    1f);

                LinearLayout textCol = new(this) { Orientation = Orientation.Vertical };
                TextView nameTv = Label(ResolveModularBlockLabel(blockId), 13.5f, OneUiTheme.TextPrimary);
                nameTv.SetTypeface(null, TypefaceStyle.Bold);
                nameTv.SetSingleLine(true);
                nameTv.Ellipsize = TextUtils.TruncateAt.End;
                textCol.AddView(nameTv);

                if (entry.Locked) {
                    TextView lockHint = Label(
                        entry.PinnedTop ? S["ModBlockPinnedTag"] : S["ModBlockEssentialTag"],
                        10.5f,
                        OneUiTheme.PrimaryHigh);
                    lockHint.SetSingleLine(true);
                    textCol.AddView(lockHint);
                }
                row.AddView(textCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
                    RightMargin = Dp(6)
                });

                bool canUp = active.CanMoveUp(blockId);
                bool canDown = active.CanMoveDown(blockId);

                Button upBtn = MakeModularMoveBtn("↑", canUp, () => {
                    if (active.MoveUp(blockId)) {
                        PersistAndApply();
                        RebuildRows();
                    }
                });
                Button downBtn = MakeModularMoveBtn("↓", canDown, () => {
                    if (active.MoveDown(blockId)) {
                        PersistAndApply();
                        RebuildRows();
                    }
                });

                row.AddView(upBtn, new LinearLayout.LayoutParams(Dp(44), Dp(44)) { RightMargin = Dp(4) });
                row.AddView(downBtn, new LinearLayout.LayoutParams(Dp(44), Dp(44)) { RightMargin = Dp(6) });

                Switch visSw = new(this) {
                    Checked = entry.Locked || entry.Visible,
                    Enabled = !entry.Locked
                };
                visSw.SetMinimumWidth(Dp(48));
                visSw.SetMinimumHeight(Dp(44));
                visSw.CheckedChange += (_, ev) => {
                    if (active.SetVisible(blockId, ev.IsChecked)) {
                        PersistAndApply();
                    }
                };
                row.AddView(visSw, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

                listHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(7)
                });
            }
        }

        RebuildRows();

        LinearLayout footer = HRow();
        footer.BaselineAligned = false;
        footer.SetGravity(GravityFlags.CenterVertical);

        Button resetBtn = PillButton(S["ModLayoutResetBtn"], () => {
            active.ResetFrom(defaults);
            PersistAndApply();
            RebuildRows();
            ShowToast(S["ModLayoutResetToast"]);
        });
        resetBtn.SetMinimumHeight(Dp(44));

        Button doneBtn = PrimaryButton(S["BtnDone"], () => dialog?.Dismiss());
        doneBtn.SetMinimumHeight(Dp(44));

        footer.AddView(resetBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        footer.AddView(doneBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(footer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        dialog = new AlertDialog.Builder(this)
            .SetView(root)
            .SetCancelable(true)
            .Create();
        dialog.Window?.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Transparent));
        dialog.Show();
    }

    Button MakeModularMoveBtn(string glyph, bool enabled, Action onClick) {
        Button b = new(this) {
            Text = glyph,
            TextSize = 15f,
            Enabled = enabled,
            Alpha = enabled ? 1f : 0.32f
        };
        b.SetAllCaps(false);
        b.SetPadding(0, 0, 0, 0);
        b.SetMinimumWidth(Dp(44));
        b.SetMinimumHeight(Dp(44));
        b.SetTextColor(enabled ? OneUiTheme.TextPrimary : OneUiTheme.TextMuted);
        b.Background = OneUiTheme.CreateRoundedDrawable(
            this,
            enabled ? OneUiTheme.SurfaceElevated : OneUiTheme.SurfaceModalRow,
            12f,
            OneUiTheme.BorderSubtle,
            1f);
        if (enabled) {
            OneUiTheme.AttachRecoil(b, 0.90f);
            b.Click += (_, _) => onClick?.Invoke();
        }
        return b;
    }

    LinearLayout BuildSettingsModularLayoutCard() {
        LinearLayout card = Card(S["ModLayoutCardTitle"], OneUiIconType.Sliders, OneUiTheme.IconSpatial);
        TextView desc = Label(S["ModLayoutCardDesc"], 12f, OneUiTheme.TextSub);
        card.AddView(desc, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });

        LinearLayout btnRow = HRow();
        btnRow.BaselineAligned = false;
        Button playBtn = PillButton(S["ModLayoutEditPlayBtn"], () => ShowModularLayoutEditorDialog(isRoomTab: false));
        playBtn.SetMinimumHeight(Dp(44));
        Button roomBtn = PillButton(S["ModLayoutEditRoomBtn"], () => ShowModularLayoutEditorDialog(isRoomTab: true));
        roomBtn.SetMinimumHeight(Dp(44));

        btnRow.AddView(playBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        btnRow.AddView(roomBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(btnRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return card;
    }
}
