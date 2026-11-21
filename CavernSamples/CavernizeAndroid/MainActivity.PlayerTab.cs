using System.Numerics;

using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Utilities;

using Cavernize.Logic.Models;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

public partial class MainActivity {
    RoomSimulatorView roomView;
    LinearLayout roomLockBtn;
    OneUiIconView roomLockIcon;
    TextView roomLockText;
    Button fineTuneToggleBtn;
    LinearLayout fineTuneContainer;
    bool isFineTuneExpanded;
    OneUiSeekBar seekBarPosX, seekBarPosZ, seekBarPosY, seekBarYaw, seekBarPitch;
    OneUiWaveMediaSeekBar playbackSeekBar;
    OneUiSeekBar seekBarVolume, seekBarLfe;
    TextView roomTelemetryText;
    LinearLayout roomTransportPlayBtn;
    OneUiIconView roomTransportPlayIcon;
    LinearLayout roomTransportLoopBtn;
    OneUiIconView roomTransportLoopIcon;
    sealed class MixerRowRef {
        public TrackSourceItem Item;
        public LinearLayout Row;
        public TextView SubTv;
        public Button SoloBtn;
        public Button MuteBtn;
        public bool LastSoloVisual;
        public bool LastMuteVisual;
    }

    OneUiInlineSelector rolloffSelector;
    LinearLayout mixerContainer;
    readonly List<MixerRowRef> mixerRowRefs = [];
    readonly Dictionary<int, long> ceilingActiveSinceMs = [];
    readonly Dictionary<int, long> ceilingPinUntilMs = [];
    long lastMixerReorderMs;
    bool pendingMixerReorder;
    bool roomScrollMixerHooked;
    const long CeilingPromoteHoldMs = 350;
    const long CeilingPinHoldMs = 1500;
    const long MixerReorderCooldownMs = 450;

    LinearLayout bentoRow1;
    bool? lastDistTileVisible;
    TextView roomBinauralProfileText;
    Button filterAllBtn, filter3DBtn, filterHeightBtn, filterBedsBtn;
    bool isUserSeeking;

    Action refreshBentoTilesUi;

    LinearLayout BuildPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(6), Dp(14), Dp(116));

        // 0. Active File Source Bar (Clean workspace context & track selector)
        page.AddView(CreateWorkspaceSourceBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 1. 3D Room Simulator Card (One UI 9 Frosted Surface)
        LinearLayout roomCard = Card();

        LinearLayout roomHeader = HRow();
        roomHeader.SetPadding(0, 0, 0, Dp(6));
        roomHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Room3D, OneUiTheme.IconSpatial, 30));
        TextView roomTitleTv = SectionTitle(S["RoomTitle"]);
        roomTitleTv.SetPadding(Dp(8), 0, Dp(8), 0);
        roomHeader.AddView(roomTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        roomLockBtn = HRow();
        roomLockBtn.SetPadding(Dp(12), Dp(7), Dp(12), Dp(7));
        OneUiTheme.AttachRecoil(roomLockBtn, 0.94f);
        roomLockIcon = new OneUiIconView(this, OneUiIconType.Unlock, ColText);
        roomLockText = Label("", 11.5f, ColText);
        roomLockText.SetIncludeFontPadding(false);
        roomLockText.SetSingleLine(true);
        roomLockText.SetTypeface(null, TypefaceStyle.Bold);
        roomLockBtn.AddView(roomLockIcon, new LinearLayout.LayoutParams(Dp(14), Dp(14)) { RightMargin = Dp(5) });
        roomLockBtn.AddView(roomLockText);
        roomLockBtn.Click += (_, _) => {
            store.RoomPositionLocked = !store.RoomPositionLocked;
            if (roomView != null) {
                roomView.IsPositionLocked = store.RoomPositionLocked;
                roomView.Invalidate();
            }
            UpdateRoomLockButtonUi();
        };
        roomHeader.AddView(roomLockBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        roomCard.AddView(roomHeader);

        TextView hintTv = Label(S["RoomTouchHint"], 11.5f, OneUiTheme.TextMuted);
        hintTv.SetPadding(0, 0, 0, Dp(6));
        roomCard.AddView(hintTv);

        roomView = new RoomSimulatorView(this) {
            IsPositionLocked = store.RoomPositionLocked
        };
        UpdateRoomLockButtonUi();
        roomView.ListenerPositionChanged += pos => {
            playerEngine.ListenerPosition = pos;
            SyncRoomSlidersFromEngine();
            UpdateRoomTelemetryText();
        };
        roomView.ListenerRotationChanged += rot => {
            playerEngine.ListenerRotation = new Vector3(rot.X, rot.Y, 0);
            SyncRoomSlidersFromEngine();
            UpdateRoomTelemetryText();
        };
        roomView.SourceSoloToggled += _ => {
            SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
            RefreshFilterButtonsUi();
            refreshBentoTilesUi?.Invoke();
        };
        roomCard.AddView(roomView);

        // Seat Presets Pill Row
        HorizontalScrollView presetScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout presetRow = HRow();
        presetRow.SetPadding(0, Dp(10), 0, Dp(6));
        (string label, Func<Vector3, Vector3> posFn)[] presets = [
            (S["PresetCenter"], e => Vector3.Zero),
            (S["PresetFront"], e => new Vector3(0, 0, e.Z * 0.65f)),
            (S["PresetBack"], e => new Vector3(0, 0, -e.Z * 0.65f)),
            (S["PresetLeft"], e => new Vector3(-e.X * 0.65f, 0, 0)),
            (S["PresetRight"], e => new Vector3(e.X * 0.65f, 0, 0)),
            (S["PresetCeiling"], e => new Vector3(0, e.Y * 0.65f, 0)),
        ];
        foreach (var (label, posFn) in presets) {
            Button pb = PillButton(label, () => {
                playerEngine.ListenerPosition = posFn(playerEngine.EnvironmentSize);
                if (label == S["PresetCenter"]) {
                    playerEngine.ListenerRotation = Vector3.Zero;
                }
                SyncRoomSlidersFromEngine();
                UpdateRoomCanvas();
            });
            pb.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
            pb.SetMinimumHeight(Dp(36));
            presetRow.AddView(pb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        }
        presetScroll.AddView(presetRow);
        roomCard.AddView(presetScroll);

        roomTelemetryText = Label("", 11.5f, OneUiTheme.PeachText);
        roomTelemetryText.SetPadding(0, 0, 0, Dp(8));
        roomCard.AddView(roomTelemetryText);

        // Collapsible Fine-Tune Capsule Sliders + Quick Reset
        LinearLayout roomCtrlRow = HRow();
        fineTuneToggleBtn = PillButton(isFineTuneExpanded ? S["FineTuneHideBtn"] : S["FineTuneBtn"], () => {
            isFineTuneExpanded = !isFineTuneExpanded;
            SetExpandedWithAnimation(fineTuneContainer, isFineTuneExpanded);
            fineTuneToggleBtn.Text = isFineTuneExpanded ? S["FineTuneHideBtn"] : S["FineTuneBtn"];
        });
        Button resetRoomBtn = PillButton(S["RoomResetAll"], () => {
            playerEngine.ListenerPosition = Vector3.Zero;
            playerEngine.ListenerRotation = Vector3.Zero;
            headTrackingManager?.Recenter();
            SyncRoomSlidersFromEngine();
            UpdateRoomCanvas();
        });
        roomCtrlRow.AddView(fineTuneToggleBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f) { RightMargin = Dp(8) });
        roomCtrlRow.AddView(resetRoomBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        roomCard.AddView(roomCtrlRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        fineTuneContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isFineTuneExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        fineTuneContainer.SetPadding(0, Dp(10), 0, 0);

        TextView resetHint = Label(S["SliderResetHint"], 11f, ColMuted);
        resetHint.SetPadding(Dp(4), 0, 0, Dp(8));
        fineTuneContainer.AddView(resetHint);

        (seekBarPosX, _) = AddSliderRow(fineTuneContainer, S["PosX"], -100, 100, 0, v => {
            float nx = v / 100f;
            playerEngine.ListenerPosition = new Vector3(nx * playerEngine.EnvironmentSize.X, playerEngine.ListenerPosition.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true, defaultValue: 0);
        (seekBarPosZ, _) = AddSliderRow(fineTuneContainer, S["PosZ"], -100, 100, 0, v => {
            float nz = v / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, playerEngine.ListenerPosition.Y, nz * playerEngine.EnvironmentSize.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true, defaultValue: 0);
        (seekBarPosY, _) = AddSliderRow(fineTuneContainer, S["PosY"], -100, 100, 0, v => {
            float ny = v / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, ny * playerEngine.EnvironmentSize.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true, defaultValue: 0);
        (seekBarYaw, _) = AddSliderRow(fineTuneContainer, S["RotYaw"], -180, 180, 0, v => {
            playerEngine.ListenerRotation = new Vector3(playerEngine.ListenerRotation.X, v, 0);
            UpdateRoomCanvas();
        }, "°", defaultValue: 0);
        (seekBarPitch, _) = AddSliderRow(fineTuneContainer, S["RotPitch"], -90, 90, 0, v => {
            playerEngine.ListenerRotation = new Vector3(v, playerEngine.ListenerRotation.Y, 0);
            UpdateRoomCanvas();
        }, "°", defaultValue: 0);
        roomCard.AddView(fineTuneContainer);
        page.AddView(roomCard);

        // 2. Transport Card featuring One UI 9 WaveSeekbar
        LinearLayout transportCard = Card(S["TimelineLabel"], OneUiIconType.Play, OneUiTheme.IconSliderMedia);
        playbackSeekBar = new OneUiWaveMediaSeekBar(this) {
            Max = 1000,
            CurrentTimeText = "00:00",
            TotalTimeText = "00:00"
        };
        playbackSeekBar.SetThemeByColor(OneUiTheme.Primary);
        playbackSeekBar.StartTrackingTouch += () => isUserSeeking = true;
        playbackSeekBar.ProgressChanged += (prog, fromUser) => {
            if (fromUser && playerEngine.TotalSeconds > 0) {
                double preview = prog / 1000.0 * playerEngine.TotalSeconds;
                string curStr = $"{TimeSpan.FromSeconds(preview):mm\\:ss}";
                string totStr = $"{TimeSpan.FromSeconds(playerEngine.TotalSeconds):mm\\:ss}";
                playbackSeekBar.CurrentTimeText = curStr;
                playbackSeekBar.TotalTimeText = totStr;
                playbackSeekBar.Invalidate();
                if (musicWaveSeekBar != null) {
                    musicWaveSeekBar.Progress = prog;
                    musicWaveSeekBar.CurrentTimeText = curStr;
                    musicWaveSeekBar.TotalTimeText = totStr;
                    musicWaveSeekBar.Invalidate();
                }
            }
        };
        playbackSeekBar.StopTrackingTouch += () => {
            isUserSeeking = false;
            if (playerEngine.TotalSeconds > 0) {
                SeekAbsoluteSeconds(playbackSeekBar.Progress / 1000.0 * playerEngine.TotalSeconds);
            }
        };
        transportCard.AddView(playbackSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });

        LinearLayout btnRow = HRow();
        btnRow.SetGravity(GravityFlags.CenterHorizontal | GravityFlags.CenterVertical);
        btnRow.SetPadding(0, Dp(2), 0, Dp(2));

        LinearLayout MakeRoomGlassBtn(OneUiIconType iconType, int iconDp, bool isCenterPlay, Color tint, Action onClick, out OneUiIconView iconOut) {
            LinearLayout btn = new(this) { Orientation = Orientation.Horizontal };
            btn.SetGravity(GravityFlags.Center);
            btn.Background = isCenterPlay
                ? OneUiTheme.CreateGlassCardDrawable(this, Color.Argb(58, 255, 255, 255), 999f, OneUiTheme.WithAlpha(ColAccent, 185))
                : OneUiTheme.CreateGlassCardDrawable(this, Color.Argb(30, 255, 255, 255), 999f, Color.Argb(46, 255, 255, 255));
            OneUiTheme.AttachRecoil(btn, 0.88f);
            iconOut = new OneUiIconView(this, iconType, tint);
            btn.AddView(iconOut, new LinearLayout.LayoutParams(Dp(iconDp), Dp(iconDp)));
            btn.Click += (_, _) => onClick?.Invoke();
            return btn;
        }

        LinearLayout restartBtn = MakeRoomGlassBtn(OneUiIconType.RewindStart, 19, false, Color.Argb(215, 252, 252, 255), StopPlayback, out _);
        LinearLayout back10Btn = MakeRoomGlassBtn(OneUiIconType.Rewind10, 21, false, Color.Argb(200, 252, 252, 255), () => SeekRelative(-10), out _);
        roomTransportPlayBtn = MakeRoomGlassBtn(OneUiIconType.Play, 29, true, Color.White, TogglePlayPause, out roomTransportPlayIcon);
        LinearLayout fwd10Btn = MakeRoomGlassBtn(OneUiIconType.Forward10, 21, false, Color.Argb(200, 252, 252, 255), () => SeekRelative(10), out _);
        roomTransportLoopBtn = MakeRoomGlassBtn(OneUiIconType.RepeatLoop, 19, false, Color.Argb(215, 252, 252, 255), ToggleLoopMode, out roomTransportLoopIcon);

        btnRow.AddView(restartBtn, new LinearLayout.LayoutParams(Dp(42), Dp(42)) { RightMargin = Dp(10) });
        btnRow.AddView(back10Btn, new LinearLayout.LayoutParams(Dp(46), Dp(46)) { RightMargin = Dp(12) });
        btnRow.AddView(roomTransportPlayBtn, new LinearLayout.LayoutParams(Dp(60), Dp(60)) { RightMargin = Dp(12) });
        btnRow.AddView(fwd10Btn, new LinearLayout.LayoutParams(Dp(46), Dp(46)) { RightMargin = Dp(10) });
        btnRow.AddView(roomTransportLoopBtn, new LinearLayout.LayoutParams(Dp(42), Dp(42)));
        transportCard.AddView(btnRow);
        RefreshRoomTransportButtons();
        page.AddView(transportCard);

        // 3. One UI 9 Quick Spatial Panel Card (1x2 Bento Toggles + Fat Dynamic Capsules + In-App Rolloff Selector)
        LinearLayout quickCard = Card(S["QuickPanelTitle"], OneUiIconType.Sliders, OneUiTheme.IconAtmosUpmix);

        bentoRow1 = HRow();
        bentoRow1.SetPadding(0, 0, 0, Dp(10));

        var tileHrtf = CreateBentoToggle(
            OneUiIconType.Headphones, S["BentoHrtf"],
            () => store.HeadphoneVirtualizer,
            () => {
                bool next = !store.HeadphoneVirtualizer;
                store.HeadphoneVirtualizer = next;
                playerEngine.HeadphoneVirtualizer = next;
                RefreshBinauralProfileUi();
                RefreshMusicPlayerControlsUi();
            },
            OneUiTheme.IconSpatial,
            helpBody: S["TipHrtf"]);

        var tileDist = CreateBentoActionTile(
            OneUiIconType.Distance, S["BentoDist"],
            () => Services.BinauralProfileService.GetActiveDistanceShortText(store, S),
            () => store.HeadphoneVirtualizer,
            CycleThKolnDistance,
            OneUiTheme.IconSliderMedia,
            helpBody: S["TipDistSim"]);

        bentoRow1.AddView(tileHrtf.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        bentoRow1.AddView(tileDist.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        quickCard.AddView(bentoRow1);

        LinearLayout roomProfRow = HRow();
        roomProfRow.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
        roomProfRow.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        OneUiTheme.AttachRecoil(roomProfRow, 0.98f);
        roomProfRow.AddView(new OneUiIconView(this, OneUiIconType.Headphones, OneUiTheme.IconSpatial),
            new LinearLayout.LayoutParams(Dp(17), Dp(17)) { RightMargin = Dp(8) });
        roomBinauralProfileText = Label(Services.BinauralProfileService.GetActiveDisplayTitle(store, S), 12.5f, ColText);
        roomBinauralProfileText.SetIncludeFontPadding(false);
        roomBinauralProfileText.Typeface = OneUiTheme.GetAppleFontMedium(this);
        roomBinauralProfileText.SetSingleLine(true);
        roomBinauralProfileText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomProfRow.AddView(roomBinauralProfileText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        roomProfRow.AddView(new OneUiIconView(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub), new LinearLayout.LayoutParams(Dp(15), Dp(15)));
        roomProfRow.Click += (_, _) => ShowBinauralProfileSelectorDialog();
        quickCard.AddView(roomProfRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) });

        refreshBentoTilesUi = () => {
            bool showDist = Services.BinauralProfileService.HasVariableDistance(store);
            if (lastDistTileVisible.HasValue && lastDistTileVisible.Value != showDist && bentoRow1 != null && bentoRow1.IsLaidOut) {
                try {
                    Android.Transitions.TransitionSet ts = new();
                    ts.SetOrdering(Android.Transitions.TransitionOrdering.Together);
                    Android.Transitions.ChangeBounds cb = new();
                    cb.SetDuration(280);
                    Android.Transitions.Fade fd = new();
                    fd.SetDuration(200);
                    ts.AddTransition(cb);
                    ts.AddTransition(fd);
                    ts.SetInterpolator(CreateOneUiEase());
                    Android.Transitions.TransitionManager.BeginDelayedTransition(bentoRow1, ts);
                } catch { }
            }
            lastDistTileVisible = showDist;

            tileDist.view.Visibility = showDist ? ViewStates.Visible : ViewStates.Gone;
            if (tileHrtf.view.LayoutParameters is LinearLayout.LayoutParams hlp) {
                int targetRight = showDist ? Dp(8) : 0;
                if (hlp.RightMargin != targetRight) {
                    hlp.RightMargin = targetRight;
                    tileHrtf.view.LayoutParameters = hlp;
                }
            }
            tileHrtf.refresh();
            if (showDist) {
                tileDist.refresh();
            }
        };
        refreshBentoTilesUi();

        (seekBarVolume, _) = AddSliderRow(quickCard, S["MasterVol"], 0, 200, (int)Math.Round(store.PlayerVolume * 100),
            v => SetMasterVolumePercent(v), "%", defaultValue: 100);

        (seekBarLfe, _) = AddSliderRow(quickCard, S["LfeVol"], 0, 200, (int)Math.Round(store.LfeVolume * 100), v => {
            float vol = v / 100f;
            store.LfeVolume = vol;
            playerEngine.LFEVolume = vol;
        }, "%", defaultValue: 100);

        AddDivider(quickCard);
        rolloffSelector = new OneUiInlineSelector(this, S["RolloffLabel"]);
        string[] rolloffNames = ["Disabled", "Logarithmic", "Linear", "Real (1/d)"];
        rolloffSelector.SetItems(rolloffNames, Math.Clamp(store.VolumeRolloffIndex, 0, 3));
        rolloffSelector.SelectionChanged += pos => {
            store.VolumeRolloffIndex = pos;
            playerEngine.VolumeRolloff = (Rolloffs)pos;
        };
        quickCard.AddView(WrapControlWithHelp(rolloffSelector, S["RolloffLabel"].TrimEnd(':'), S["TipRolloff"]));
        page.AddView(quickCard);

        // 4. Live Channel & 3D Object Mixer Card (Filter buttons visibly toggle Solo state)
        LinearLayout mixerCard = Card(S["MixerTitle"], OneUiIconType.Queue, OneUiTheme.IconStudioExport);

        HorizontalScrollView filterScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout filterRow = HRow();
        filterAllBtn = FilterBtn(S["FilterAll"], ClearAllSoloFilters);
        filter3DBtn = FilterBtn(S["Filter3DOnly"], () => ToggleCategorySolo(item => item.IsDynamicObject, flag => {
            store.MuteBed = flag;
            playerEngine.MuteBed = flag;
        }));
        filterHeightBtn = FilterBtn(S["FilterHeightOnly"], ToggleDynamicCeilingFilter);
        filterBedsBtn = FilterBtn(S["FilterBedsOnly"], () => ToggleCategorySolo(item => item.IsBed, flag => {
            playerEngine.OnlyBeds = flag;
        }));
        filterRow.AddView(filterAllBtn);
        filterRow.AddView(filter3DBtn);
        filterRow.AddView(filterHeightBtn);
        filterRow.AddView(filterBedsBtn);
        filterScroll.AddView(filterRow);
        mixerCard.AddView(filterScroll);

        mixerContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        mixerContainer.SetPadding(0, Dp(8), 0, 0);
        mixerCard.AddView(mixerContainer);
        page.AddView(mixerCard);

        RefreshFilterButtonsUi();
        return page;
    }

    (LinearLayout view, Action refresh) CreateBentoToggle(
        OneUiIconType iconType,
        string title,
        Func<bool> getState,
        Action onToggle,
        Color activeColor,
        string helpBody = null) {
        LinearLayout tile = HRow();
        tile.SetMinimumHeight(Dp(62));
        tile.SetPadding(Dp(12), Dp(10), Dp(8), Dp(10));
        OneUiTheme.AttachRecoil(tile, 0.95f);

        OneUiIconBadge badge = OneUiTheme.CreateVectorIconBadge(this, iconType, activeColor, 34);
        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = Label(title, 12.5f, ColText);
        titleTv.SetIncludeFontPadding(false);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);
        TextView stateTv = Label("", 11f, OneUiTheme.TextSub);
        col.AddView(titleTv);
        col.AddView(stateTv);

        tile.AddView(badge, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(9) });
        tile.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        if (!string.IsNullOrEmpty(helpBody)) {
            tile.AddView(CreateInlineHelpBtn(title, helpBody), new LinearLayout.LayoutParams(Dp(24), Dp(24)) { LeftMargin = Dp(4) });
        }

        void Refresh() {
            bool on = getState();
            Color tileTint = on
                ? Color.Argb(92, activeColor.R, activeColor.G, activeColor.B)
                : OneUiTheme.SurfaceSubTile;
            tile.Background = OneUiTheme.CreateGlassCardDrawable(
                this,
                tileTint,
                OneUiTheme.TileCornerDp,
                on ? activeColor : null);
            badge.UpdateColors(on ? activeColor : OneUiTheme.ControlStroke, ColText, solidOverride: on);
            stateTv.Text = on ? "ON" : "OFF";
            stateTv.SetTextColor(on ? ColText : OneUiTheme.TextMuted);
        }

        tile.Click += (_, _) => {
            onToggle();
            Refresh();
            refreshBentoTilesUi?.Invoke();
        };

        Refresh();
        return (tile, Refresh);
    }

    (LinearLayout view, Action refresh) CreateBentoActionTile(
        OneUiIconType iconType,
        string title,
        Func<string> getSubtitle,
        Func<bool> getActive,
        Action onClick,
        Color activeColor,
        string helpBody = null) {
        LinearLayout tile = HRow();
        tile.SetMinimumHeight(Dp(62));
        tile.SetPadding(Dp(12), Dp(10), Dp(8), Dp(10));
        OneUiTheme.AttachRecoil(tile, 0.95f);

        OneUiIconBadge badge = OneUiTheme.CreateVectorIconBadge(this, iconType, activeColor, 34);
        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = Label(title, 12.5f, ColText);
        titleTv.SetIncludeFontPadding(false);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        TextView stateTv = Label("", 10.8f, OneUiTheme.TextSub);
        stateTv.SetSingleLine(true);
        stateTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        col.AddView(titleTv);
        col.AddView(stateTv);

        tile.AddView(badge, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(9) });
        tile.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        if (!string.IsNullOrEmpty(helpBody)) {
            tile.AddView(CreateInlineHelpBtn(title, helpBody), new LinearLayout.LayoutParams(Dp(24), Dp(24)) { LeftMargin = Dp(4) });
        }

        void Refresh() {
            bool on = getActive();
            Color tileTint = on
                ? Color.Argb(78, activeColor.R, activeColor.G, activeColor.B)
                : OneUiTheme.SurfaceSubTile;
            tile.Background = OneUiTheme.CreateGlassCardDrawable(
                this,
                tileTint,
                OneUiTheme.TileCornerDp,
                on ? OneUiTheme.WithAlpha(activeColor, 175) : null);
            badge.UpdateColors(on ? activeColor : OneUiTheme.ControlStroke, ColText, solidOverride: on);
            string nextSub = getSubtitle();
            bool textChanged = !string.IsNullOrEmpty(stateTv.Text) && !string.Equals(stateTv.Text, nextSub, StringComparison.Ordinal);
            stateTv.Text = nextSub;
            stateTv.SetTextColor(on ? ColText : OneUiTheme.TextMuted);
            if (textChanged && tile.IsLaidOut) {
                stateTv.Animate()?.Cancel();
                stateTv.Alpha = 0.35f;
                stateTv.TranslationY = Dp(3);
                stateTv.Animate()
                    .Alpha(1f)
                    .TranslationY(0f)
                    .SetDuration(190)
                    .SetInterpolator(CreateOneUiEase())
                    .Start();
            }
        }

        tile.Click += (_, _) => onClick?.Invoke();
        Refresh();
        return (tile, Refresh);
    }

    Button FilterBtn(string text, Action onClick) {
        Button b = PillButton(text, onClick);
        b.TextSize = 12f;
        b.SetPadding(Dp(13), Dp(7), Dp(13), Dp(7));
        b.SetMinimumHeight(Dp(38));
        b.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            RightMargin = Dp(6)
        };
        return b;
    }

    void ClearAllSoloFilters() {
        playerEngine?.ClearSoloMute();
        if (playerEngine != null) {
            playerEngine.DynamicCeilingOnly = false;
        }
        ceilingActiveSinceMs.Clear();
        ceilingPinUntilMs.Clear();
        pendingMixerReorder = false;
        foreach (TrackSourceItem item in trackSourceItems) {
            item.IsSolo = false;
            item.IsMuted = false;
            item.IsDynamicallyFilteredOut = false;
        }
        store.MuteBed = false;
        store.MuteGround = false;
        playerEngine.MuteBed = false;
        playerEngine.MuteGround = false;
        playerEngine.OnlyBeds = false;
        SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
        RefreshFilterButtonsUi();
        refreshBentoTilesUi?.Invoke();
        RefreshSettingsTabButtons();
        UpdateRoomCanvas();
    }

    void ToggleDynamicCeilingFilter() {
        if (playerEngine == null) {
            return;
        }
        bool next = !playerEngine.DynamicCeilingOnly;
        ceilingActiveSinceMs.Clear();
        ceilingPinUntilMs.Clear();
        pendingMixerReorder = false;
        store.MuteBed = false;
        store.MuteGround = false;
        playerEngine.MuteBed = false;
        playerEngine.MuteGround = false;
        playerEngine.OnlyBeds = false;
        for (int i = 0; i < trackSourceItems.Count; i++) {
            trackSourceItems[i].IsSolo = false;
            if (!next) {
                trackSourceItems[i].IsDynamicallyFilteredOut = false;
            }
        }
        playerEngine.DynamicCeilingOnly = next;
        SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
        RefreshFilterButtonsUi();
        refreshBentoTilesUi?.Invoke();
        RefreshSettingsTabButtons();
        UpdateRoomCanvas();
    }

    bool IsCategorySoloActive(Func<TrackSourceItem, bool> predicate) {
        if (trackSourceItems == null || trackSourceItems.Count == 0) {
            return false;
        }
        bool anyMatch = false;
        for (int i = 0; i < trackSourceItems.Count; i++) {
            bool matches = predicate(trackSourceItems[i]);
            if (matches) {
                anyMatch = true;
            }
            if (trackSourceItems[i].IsSolo != matches) {
                return false;
            }
        }
        return anyMatch;
    }

    void ToggleCategorySolo(Func<TrackSourceItem, bool> predicate, Action<bool> fallbackWhenNoTrack) {
        if (playerEngine != null) {
            playerEngine.DynamicCeilingOnly = false;
        }
        ceilingActiveSinceMs.Clear();
        ceilingPinUntilMs.Clear();
        pendingMixerReorder = false;
        if (trackSourceItems == null || trackSourceItems.Count == 0) {
            fallbackWhenNoTrack?.Invoke(true);
            refreshBentoTilesUi?.Invoke();
            RefreshFilterButtonsUi();
            RefreshSettingsTabButtons();
            UpdateRoomCanvas();
            return;
        }

        bool alreadyActive = IsCategorySoloActive(predicate);
        // Clear hard category mutes so individual visual Solo state controls what plays and renders
        store.MuteBed = false;
        store.MuteGround = false;
        playerEngine.MuteBed = false;
        playerEngine.MuteGround = false;
        playerEngine.OnlyBeds = false;

        if (alreadyActive) {
            for (int i = 0; i < trackSourceItems.Count; i++) {
                trackSourceItems[i].IsSolo = false;
                trackSourceItems[i].IsDynamicallyFilteredOut = false;
            }
        } else {
            int matchedCount = 0;
            for (int i = 0; i < trackSourceItems.Count; i++) {
                bool m = predicate(trackSourceItems[i]);
                trackSourceItems[i].IsSolo = m;
                trackSourceItems[i].IsDynamicallyFilteredOut = false;
                if (m) {
                    matchedCount++;
                }
            }
            if (matchedCount == 0) {
                Toast.MakeText(this, S["FilterNoMatchToast"], ToastLength.Short).Show();
            }
        }

        SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
        RefreshFilterButtonsUi();
        refreshBentoTilesUi?.Invoke();
        RefreshSettingsTabButtons();
        UpdateRoomCanvas();
    }

    void RefreshFilterButtonsUi() {
        bool dynCeil = playerEngine?.DynamicCeilingOnly ?? false;
        bool solo3D = !dynCeil && IsCategorySoloActive(item => item.IsDynamicObject);
        bool soloBeds = !dynCeil && IsCategorySoloActive(item => item.IsBed);
        bool noneActive = !dynCeil && !solo3D && !soloBeds && !trackSourceItems.Any(x => x.IsSolo);

        void Style(Button b, bool active, Color accent) {
            if (b == null) return;
            b.Background = active
                ? OneUiTheme.CreateRippleRoundedDrawable(this, accent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        Style(filterAllBtn, noneActive, OneUiTheme.IconSpatial);
        Style(filter3DBtn, solo3D, OneUiTheme.IconAtmosUpmix);
        Style(filterHeightBtn, dynCeil, OneUiTheme.IconStemsCalib);
        Style(filterBedsBtn, soloBeds, OneUiTheme.IconStudioExport);
    }

    void UpdateRoomLockButtonUi() {
        if (roomLockBtn == null) {
            return;
        }
        bool locked = store.RoomPositionLocked;
        roomLockIcon.IconType = locked ? OneUiIconType.Lock : OneUiIconType.Unlock;
        roomLockText.Text = locked ? S["UnlockRoom"] : S["LockRoom"];
        roomLockBtn.Background = locked
            ? OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.Primary, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
    }

    void TogglePlayPause() {
        CavernizeTrack active = GetActiveTrack();
        if (active == null || !active.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        if (active.Codec == Cavern.Format.Common.Codec.TrueHD) {
            PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: true);
            return;
        }
        if (playerEngine.IsPlaying) {
            playerEngine.IsPlaying = false;
            audioOutput.Pause();
            SyncPlaybackUi(false, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
            mediaNotification?.UpdatePlaybackState(false, playerEngine.CurrentSeconds, showNotification: true);
        } else {
            if (playerEngine.TotalSeconds > 0 && playerEngine.CurrentSeconds >= playerEngine.TotalSeconds - 0.05) {
                playerEngine.Seek(0);
            }
            playerEngine.IsPlaying = true;
            audioOutput.Start();
            SyncPlaybackUi(true, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
            mediaNotification?.UpdatePlaybackState(true, playerEngine.CurrentSeconds, showNotification: true);
        }
    }

    void StopPlayback() {
        if (playerEngine != null) {
            playerEngine.IsPlaying = false;
            playerEngine.Seek(0);
        }
        audioOutput?.Stop();
        musicVideoStage?.SeekTo(0);
        musicVideoStage?.SyncPlaybackState(false, 0);
        mediaNotification?.UpdatePlaybackState(false, 0, showNotification: false);
        SyncPlaybackUi(false, 0, playerEngine?.TotalSeconds ?? 0);
    }

    void SeekRelative(double deltaSeconds) {
        if (selectedTrack == null || playerEngine.TotalSeconds <= 0) {
            return;
        }
        SeekAbsoluteSeconds(playerEngine.CurrentSeconds + deltaSeconds);
    }

    void SeekAbsoluteSeconds(double seconds) {
        if (selectedTrack == null || playerEngine.TotalSeconds <= 0) {
            return;
        }
        double target = Math.Clamp(seconds, 0, Math.Max(0, playerEngine.TotalSeconds - 0.05));
        playerEngine.Seek(target);
        musicVideoStage?.SeekTo(target);
        SyncPlaybackUi(playerEngine.IsPlaying, target, playerEngine.TotalSeconds);
        mediaNotification?.UpdatePlaybackState(playerEngine.IsPlaying, target, showNotification: true);
    }

    void OnPlayerCompleted() {
        if (activeLibraryQueue.Count > 1 && currentMediaMeta?.HasVideo != true && !(playerEngine?.Loop ?? false)) {
            PlayLibraryQueueStep(+1);
            return;
        }
        SyncPlaybackUi(false, playerEngine?.TotalSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
        mediaNotification?.UpdatePlaybackState(false, playerEngine?.TotalSeconds ?? 0, showNotification: selectedTrack != null);
    }

    void ToggleLoopMode() {
        if (playerEngine == null) {
            return;
        }
        playerEngine.Loop = !playerEngine.Loop;
        RefreshRoomTransportButtons();
        RefreshMusicPlayerControlsUi();
    }

    void RefreshRoomTransportButtons() {
        bool isPlaying = playerEngine?.IsPlaying ?? false;
        if (roomTransportPlayIcon != null) {
            roomTransportPlayIcon.IconType = isPlaying ? OneUiIconType.Pause : OneUiIconType.Play;
        }
        if (roomTransportLoopBtn != null && roomTransportLoopIcon != null) {
            bool loop = playerEngine?.Loop ?? false;
            roomTransportLoopBtn.Background = loop
                ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(ColAccent, 90), 99f, ColAccent)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 99f);
            roomTransportLoopIcon.IconColor = loop ? ColText : OneUiTheme.TextSub;
        }
    }

    void SyncPlaybackUi(bool isPlaying, double curSeconds, double totSeconds) {
        RefreshRoomTransportButtons();
        int prog = totSeconds > 0 ? Math.Clamp((int)(curSeconds / totSeconds * 1000.0), 0, 1000) : 0;
        string curStr = $"{TimeSpan.FromSeconds(Math.Max(0, curSeconds)):mm\\:ss}";
        string totStr = $"{TimeSpan.FromSeconds(Math.Max(0, totSeconds)):mm\\:ss}";

        void ApplyToWaveBar(OneUiWaveMediaSeekBar bar) {
            if (bar == null) return;
            bar.IsWavePlaying = isPlaying;
            if (!isUserSeeking) {
                bar.Progress = prog;
                bar.CurrentTimeText = curStr;
                bar.TotalTimeText = totStr;
                bar.Invalidate();
            }
        }
        ApplyToWaveBar(playbackSeekBar);
        ApplyToWaveBar(musicWaveSeekBar);
        RefreshMusicPlayerControlsUi();
        UpdateRoomCanvas();
    }

    void SyncRoomSlidersFromEngine() {
        Vector3 env = playerEngine.EnvironmentSize;
        Vector3 pos = playerEngine.ListenerPosition;
        Vector3 rot = playerEngine.ListenerRotation;
        if (seekBarPosX != null) seekBarPosX.Progress = Math.Clamp((int)Math.Round(pos.X / env.X * 100f) + 100, 0, 200);
        if (seekBarPosZ != null) seekBarPosZ.Progress = Math.Clamp((int)Math.Round(pos.Z / env.Z * 100f) + 100, 0, 200);
        if (seekBarPosY != null) seekBarPosY.Progress = Math.Clamp((int)Math.Round(pos.Y / env.Y * 100f) + 100, 0, 200);
        if (seekBarYaw != null) seekBarYaw.Progress = Math.Clamp((int)Math.Round(rot.Y) + 180, 0, 360);
        if (seekBarPitch != null) seekBarPitch.Progress = Math.Clamp((int)Math.Round(rot.X) + 90, 0, 180);
    }

    void UpdateRoomTelemetryText() {
        if (roomTelemetryText != null && playerEngine != null) {
            Vector3 p = playerEngine.ListenerPosition;
            Vector3 r = playerEngine.ListenerRotation;
            string htSuffix = playerEngine.SceneHub.IsHeadTrackingLive
                ? $"  •  HT: {playerEngine.SceneHub.ActiveHeadTrackerName}"
                : string.Empty;
            roomTelemetryText.Text = $"X: {p.X:0.0}m  •  Y: {p.Y:0.0}m  •  Z: {p.Z:0.0}m  •  Yaw: {r.Y:0}°  •  Pitch: {r.X:0}°{htSuffix}";
        }
    }

    void UpdateRoomCanvas() {
        if (roomView == null || playerEngine == null) {
            return;
        }
        roomView.UpdateState(
            trackSourceItems,
            playerEngine.EnvironmentSize,
            playerEngine.ListenerPosition,
            playerEngine.ListenerRotation,
            playerEngine.IsPlaying);
        UpdateRoomTelemetryText();
        UpdateMixerRowsLive();
    }

    void ApplyMixerRowVisualState(MixerRowRef r, bool dynCeil, bool forceRedraw = false) {
        TrackSourceItem item = r.Item;
        bool isVisuallySolo = item.IsSolo || (dynCeil && !item.IsDynamicallyFilteredOut);
        bool isMuted = item.IsMuted;

        if (forceRedraw || isVisuallySolo != r.LastSoloVisual || isMuted != r.LastMuteVisual) {
            r.LastSoloVisual = isVisuallySolo;
            r.LastMuteVisual = isMuted;

            r.SoloBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                this, isVisuallySolo ? OneUiTheme.SoloGold : OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
            r.SoloBtn.SetTextColor(isVisuallySolo ? Color.Black : ColText);
            r.SoloBtn.SetTypeface(null, isVisuallySolo ? TypefaceStyle.Bold : TypefaceStyle.Normal);

            r.MuteBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                this, isMuted ? ColDanger : OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
            r.MuteBtn.SetTextColor(ColText);

            r.Row.Background = OneUiTheme.CreateGlassCardDrawable(
                this, OneUiTheme.SurfaceSubTile, 18f, isVisuallySolo ? OneUiTheme.SoloGold : null);
        }

        // Only manually muted rows dim; inactive rows under Solo / Ceiling stay crisp at full contrast
        float targetAlpha = isMuted ? 0.42f : 1f;
        if (MathF.Abs(r.Row.Alpha - targetAlpha) > 0.04f) {
            r.Row.Alpha = targetAlpha;
        }
    }

    bool IsMixerTopVisibleInViewport() {
        if (mixerContainer == null || tabContainers == null || tabContainers.Length <= 1) {
            return true;
        }
        if (tabContainers[1] is not ScrollView roomScroll) {
            return true;
        }
        int topInScroll = 0;
        View cur = mixerContainer;
        while (cur != null && cur != roomScroll) {
            topInScroll += cur.Top;
            cur = cur.Parent as View;
        }
        return roomScroll.ScrollY <= topInScroll + Dp(56);
    }

    void EnsureRoomScrollMixerHook() {
        if (roomScrollMixerHooked || tabContainers == null || tabContainers.Length <= 1) {
            return;
        }
        if (tabContainers[1] is ScrollView roomScroll) {
            roomScrollMixerHooked = true;
            roomScroll.ScrollChange += (_, _) => {
                if (pendingMixerReorder && activeTab == 1 && IsMixerTopVisibleInViewport()) {
                    pendingMixerReorder = false;
                    ReorderMixerRows(animate: true, forceImmediate: false);
                }
            };
        }
    }

    void SyncMixerRowsVisualAndOrder(bool animate, bool forceImmediate) {
        if (mixerContainer == null) {
            return;
        }
        if (mixerRowRefs.Count != trackSourceItems.Count) {
            RefreshPlayerSourcesList();
            return;
        }
        bool dynCeil = playerEngine?.DynamicCeilingOnly ?? false;
        for (int i = 0; i < mixerRowRefs.Count; i++) {
            ApplyMixerRowVisualState(mixerRowRefs[i], dynCeil);
        }
        ReorderMixerRows(animate, forceImmediate);
    }

    void ReorderMixerRows(bool animate, bool forceImmediate) {
        if (mixerContainer == null || mixerRowRefs.Count <= 1) {
            return;
        }
        EnsureRoomScrollMixerHook();

        long nowMs = Android.OS.SystemClock.UptimeMillis();
        bool dynCeil = playerEngine?.DynamicCeilingOnly ?? false;
        bool anyManualSolo = false;
        for (int i = 0; i < mixerRowRefs.Count; i++) {
            if (mixerRowRefs[i].Item.IsSolo) {
                anyManualSolo = true;
                break;
            }
        }

        // Anti-chaos protection for live dynamic ceiling updates:
        // 1) Pause automatic reordering while user is scrolled further down inspecting lower channels.
        // 2) Apply cooldown + promote/pin hysteresis so fast-moving ceiling objects don't bounce rows chaotically.
        if (!forceImmediate) {
            if (!IsMixerTopVisibleInViewport()) {
                pendingMixerReorder = true;
                return;
            }
            if (nowMs - lastMixerReorderMs < MixerReorderCooldownMs) {
                return;
            }
        }

        HashSet<int> promoted = [];
        if (dynCeil) {
            for (int i = 0; i < mixerRowRefs.Count; i++) {
                TrackSourceItem item = mixerRowRefs[i].Item;
                bool activeNow = !item.IsDynamicallyFilteredOut;
                if (activeNow) {
                    if (!ceilingActiveSinceMs.TryGetValue(item.Index, out long since)) {
                        since = nowMs;
                        ceilingActiveSinceMs[item.Index] = since;
                    }
                    if (forceImmediate || (nowMs - since) >= CeilingPromoteHoldMs) {
                        promoted.Add(item.Index);
                        ceilingPinUntilMs[item.Index] = nowMs + CeilingPinHoldMs;
                    }
                } else {
                    ceilingActiveSinceMs.Remove(item.Index);
                    if (!forceImmediate &&
                        ceilingPinUntilMs.TryGetValue(item.Index, out long pinUntil) &&
                        nowMs < pinUntil) {
                        promoted.Add(item.Index);
                    } else {
                        ceilingPinUntilMs.Remove(item.Index);
                    }
                }
            }
        } else if (anyManualSolo) {
            for (int i = 0; i < mixerRowRefs.Count; i++) {
                if (mixerRowRefs[i].Item.IsSolo) {
                    promoted.Add(mixerRowRefs[i].Item.Index);
                }
            }
        }

        List<MixerRowRef> desired = new(mixerRowRefs);
        desired.Sort((a, b) => {
            int pa = promoted.Contains(a.Item.Index) ? 0 : 1;
            int pb = promoted.Contains(b.Item.Index) ? 0 : 1;
            int cmp = pa.CompareTo(pb);
            return cmp != 0 ? cmp : a.Item.Index.CompareTo(b.Item.Index);
        });

        bool changed = mixerContainer.ChildCount != desired.Count;
        if (!changed) {
            for (int i = 0; i < desired.Count; i++) {
                if (mixerContainer.GetChildAt(i) != desired[i].Row) {
                    changed = true;
                    break;
                }
            }
        }
        if (!changed) {
            pendingMixerReorder = false;
            return;
        }

        lastMixerReorderMs = nowMs;
        pendingMixerReorder = false;

        if (animate && mixerContainer.IsLaidOut) {
            try {
                Android.Transitions.ChangeBounds cb = new();
                cb.SetDuration(300);
                cb.SetInterpolator(CreateOneUiEase());
                Android.Transitions.TransitionManager.BeginDelayedTransition(mixerContainer, cb);
            } catch { }
        }

        for (int i = 0; i < desired.Count; i++) {
            View row = desired[i].Row;
            if (i < mixerContainer.ChildCount && mixerContainer.GetChildAt(i) == row) {
                continue;
            }
            mixerContainer.RemoveView(row);
            mixerContainer.AddView(row, i);
        }
    }

    void UpdateMixerRowsLive() {
        if (mixerRowRefs.Count == 0 || activeTab != 1) {
            return;
        }
        bool dynCeil = playerEngine?.DynamicCeilingOnly ?? false;
        for (int i = 0; i < mixerRowRefs.Count; i++) {
            MixerRowRef r = mixerRowRefs[i];
            TrackSourceItem item = r.Item;
            ApplyMixerRowVisualState(r, dynCeil);
            if (item.IsDynamicObject || dynCeil) {
                Vector3 p = item.NormalizedPosition;
                r.SubTv.Text = dynCeil && !item.IsDynamicallyFilteredOut
                    ? $"Pos: ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) • {S["CeilingLiveBadge"]}"
                    : $"Pos: ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0})";
            }
        }
        if (dynCeil || pendingMixerReorder) {
            ReorderMixerRows(animate: true, forceImmediate: false);
        }
    }

    void RefreshPlayerSourcesList() {
        if (mixerContainer == null) {
            return;
        }
        mixerContainer.RemoveAllViews();
        mixerRowRefs.Clear();
        ceilingActiveSinceMs.Clear();
        ceilingPinUntilMs.Clear();
        pendingMixerReorder = false;
        if (trackSourceItems.Count == 0) {
            mixerContainer.AddView(Label(S["NoTrack"], 12f, ColMuted));
            return;
        }

        bool dynCeil = playerEngine?.DynamicCeilingOnly ?? false;

        foreach (TrackSourceItem item in trackSourceItems) {
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(9), Dp(10), Dp(9));
            LinearLayout.LayoutParams rlp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            };

            LinearLayout infoCol = new(this) { Orientation = Orientation.Vertical };
            TextView nameTv = Label(item.Name, 12.5f, item.IsDynamicObject ? OneUiTheme.DynamicObject : ColCyan);
            TextView subTv = Label($"Pos: ({item.NormalizedPosition.X:0.0}, {item.NormalizedPosition.Y:0.0}, {item.NormalizedPosition.Z:0.0})", 10.5f, ColMuted);
            infoCol.AddView(nameTv);
            infoCol.AddView(subTv);
            row.AddView(infoCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            Button soloBtn = new(this) { Text = "S", TextSize = 11.5f };
            Button muteBtn = new(this) { Text = "M", TextSize = 11.5f };
            soloBtn.SetIncludeFontPadding(false);
            muteBtn.SetIncludeFontPadding(false);
            soloBtn.SetMinimumWidth(0);
            muteBtn.SetMinimumWidth(0);
            soloBtn.SetMinimumHeight(Dp(34));
            muteBtn.SetMinimumHeight(Dp(34));
            soloBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            muteBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            OneUiTheme.AttachRecoil(soloBtn, 0.90f);
            OneUiTheme.AttachRecoil(muteBtn, 0.90f);

            MixerRowRef rowRef = new() {
                Item = item,
                Row = row,
                SubTv = subTv,
                SoloBtn = soloBtn,
                MuteBtn = muteBtn
            };

            soloBtn.Click += (_, _) => {
                if (playerEngine != null && playerEngine.DynamicCeilingOnly) {
                    playerEngine.DynamicCeilingOnly = false;
                    ceilingActiveSinceMs.Clear();
                    ceilingPinUntilMs.Clear();
                    for (int k = 0; k < trackSourceItems.Count; k++) {
                        trackSourceItems[k].IsDynamicallyFilteredOut = false;
                    }
                }
                item.IsSolo = !item.IsSolo;
                SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
                RefreshFilterButtonsUi();
                refreshBentoTilesUi?.Invoke();
                UpdateRoomCanvas();
            };
            muteBtn.Click += (_, _) => {
                item.IsMuted = !item.IsMuted;
                ApplyMixerRowVisualState(rowRef, playerEngine?.DynamicCeilingOnly ?? false, forceRedraw: true);
                UpdateRoomCanvas();
            };
            ApplyMixerRowVisualState(rowRef, dynCeil, forceRedraw: true);

            row.AddView(soloBtn, new LinearLayout.LayoutParams(Dp(38), ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
            row.AddView(muteBtn, new LinearLayout.LayoutParams(Dp(38), ViewGroup.LayoutParams.WrapContent));
            mixerContainer.AddView(row, rlp);
            mixerRowRefs.Add(rowRef);
        }

        ReorderMixerRows(animate: false, forceImmediate: true);
    }

    void StartTelemetryTimer() {
        int loudnessMeterTick = 0;
        void Tick() {
            if (uiDestroyed) {
                return;
            }
            if (playerEngine != null && playerEngine.IsPlaying) {
                SyncPlaybackUi(true, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
                if ((++loudnessMeterTick & 3) == 0) {
                    RefreshLiveLoudnessMeterUi();
                }
            }
            uiHandler?.PostDelayed(Tick, 65);
        }
        uiHandler?.PostDelayed(Tick, 65);
    }

    (OneUiSeekBar bar, TextView valLabel) AddSliderRow(
        LinearLayout parent,
        string label,
        int min,
        int max,
        int initial,
        Action<int> onChanged,
        string suffix = "",
        bool useBlue = false,
        int? defaultValue = null,
        string helpBody = null) {
        int def = defaultValue ?? initial;
        OneUiSeekBar bar = new(this) {
            Max = max - min,
            Progress = Math.Clamp(initial - min, 0, max - min),
            DefaultProgress = Math.Clamp(def - min, 0, max - min),
            HasDefaultReset = true,
            LabelText = label,
            ValueText = $"{initial}{suffix}",
            UseBlueAccent = useBlue
        };
        bar.ProgressChanged += (_, e) => {
            int val = e.Progress + min;
            bar.ValueText = $"{val}{suffix}";
            bar.Invalidate();
            if (e.FromUser) {
                onChanged?.Invoke(val);
            }
        };
        bar.UserProgressChanged += prog => {
            int val = prog + min;
            bar.ValueText = $"{val}{suffix}";
            bar.Invalidate();
            onChanged?.Invoke(val);
        };
        bar.ResetRequested += prog => {
            int val = prog + min;
            bar.ValueText = $"{val}{suffix}";
            bar.Invalidate();
            onChanged?.Invoke(val);
        };
        if (string.IsNullOrEmpty(helpBody)) {
            parent.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(8)
            });
        } else {
            parent.AddView(WrapControlWithHelp(bar, label, helpBody, bottomMarginDp: 8));
        }
        return (bar, null);
    }

    Switch AddSwitchRow(LinearLayout parent, string label, bool initial, Action<bool> onChanged, string helpBody = null) {
        LinearLayout row = HRow();
        row.SetPadding(0, Dp(7), 0, Dp(7));
        TextView lbl = Label(label, 13.5f, ColText);
        OneUiSwitch sw = new(this) {
            Checked = initial
        };
        sw.CheckedChange += (_, e) => onChanged?.Invoke(e.IsChecked);
        lbl.Click += (_, _) => sw.Checked = !sw.Checked;

        row.AddView(lbl, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (!string.IsNullOrEmpty(helpBody)) {
            View helpBtn = CreateInlineHelpBtn(label, helpBody);
            row.AddView(helpBtn, new LinearLayout.LayoutParams(Dp(26), Dp(26)) { LeftMargin = Dp(6), RightMargin = Dp(8) });
        } else {
            lbl.SetPadding(0, 0, Dp(10), 0);
        }
        row.AddView(sw, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        parent.AddView(row);
        return sw;
    }
}
