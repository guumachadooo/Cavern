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
    Button playPauseBtn;
    Switch loopSwitch;
    OneUiInlineSelector rolloffSelector;
    LinearLayout mixerContainer;
    Button filterAllBtn, filter3DBtn, filterHeightBtn, filterBedsBtn;
    bool isUserSeeking;

    Action refreshBentoTilesUi;

    LinearLayout BuildPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(2), Dp(14), Dp(116));

        // 1. 3D Room Simulator Card (One UI 9 Frosted Surface)
        LinearLayout roomCard = Card();

        LinearLayout roomHeader = HRow();
        roomHeader.SetPadding(0, 0, 0, Dp(6));
        roomHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Room3D, OneUiTheme.Primary, 30));
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
            RefreshPlayerSourcesList();
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

        // Collapsible Fine-Tune Capsule Sliders
        fineTuneToggleBtn = PillButton(isFineTuneExpanded ? S["FineTuneHideBtn"] : S["FineTuneBtn"], () => {
            isFineTuneExpanded = !isFineTuneExpanded;
            SetExpandedWithAnimation(fineTuneContainer, isFineTuneExpanded);
            fineTuneToggleBtn.Text = isFineTuneExpanded ? S["FineTuneHideBtn"] : S["FineTuneBtn"];
        });
        roomCard.AddView(fineTuneToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

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
        LinearLayout transportCard = Card(S["TimelineLabel"], OneUiIconType.Play, OneUiTheme.Primary);
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
        Button back10 = PillButton("-10s", () => SeekRelative(-10));
        playPauseBtn = PrimaryButton(S["Play"], TogglePlayPause);
        Button stopBtn = PillButton(S["Stop"], StopPlayback);
        Button fwd10 = PillButton("+10s", () => SeekRelative(10));
        btnRow.AddView(back10, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        btnRow.AddView(playPauseBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.45f) { RightMargin = Dp(6) });
        btnRow.AddView(stopBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        btnRow.AddView(fwd10, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        transportCard.AddView(btnRow);

        AddDivider(transportCard);
        loopSwitch = AddSwitchRow(transportCard, S["Loop"], false, val => {
            playerEngine.Loop = val;
            RefreshMusicPlayerControlsUi();
        });
        page.AddView(transportCard);

        // 3. One UI 9 Quick Spatial Panel Card (2x2 Bento Toggles + Fat Dynamic Capsules + In-App Rolloff Selector)
        LinearLayout quickCard = Card(S["QuickPanelTitle"], OneUiIconType.Sliders, OneUiTheme.FunctionalIndigo);

        LinearLayout bentoRow1 = HRow();
        LinearLayout bentoRow2 = HRow();
        bentoRow1.SetPadding(0, 0, 0, Dp(8));
        bentoRow2.SetPadding(0, 0, 0, Dp(10));

        var tileHrtf = CreateBentoToggle(
            OneUiIconType.Headphones, S["BentoHrtf"],
            () => store.HeadphoneVirtualizer,
            () => {
                bool next = !store.HeadphoneVirtualizer;
                store.HeadphoneVirtualizer = next;
                playerEngine.HeadphoneVirtualizer = next;
                RefreshMusicPlayerControlsUi();
            },
            OneUiTheme.Primary,
            helpBody: S["TipHrtf"]);

        var tileDist = CreateBentoToggle(
            OneUiIconType.Distance, S["BentoDist"],
            () => store.DistanceSimulation,
            () => {
                bool next = !store.DistanceSimulation;
                store.DistanceSimulation = next;
                playerEngine.DistanceSimulation = next;
            },
            OneUiTheme.FunctionalCyan,
            helpBody: S["TipDistSim"]);

        var tile3DOnly = CreateBentoToggle(
            OneUiIconType.Sparkles, S["Bento3DOnly"],
            () => IsCategorySoloActive(item => item.IsDynamicObject) || store.MuteBed,
            () => ToggleCategorySolo(item => item.IsDynamicObject, flag => {
                store.MuteBed = flag;
                playerEngine.MuteBed = flag;
            }),
            OneUiTheme.CoralStart);

        var tileHeight = CreateBentoToggle(
            OneUiIconType.HeightUp, S["BentoHeightOnly"],
            () => IsCategorySoloActive(item => item.NormalizedPosition.Y > 0.15f) || store.MuteGround,
            () => ToggleCategorySolo(item => item.NormalizedPosition.Y > 0.15f, flag => {
                store.MuteGround = flag;
                playerEngine.MuteGround = flag;
            }),
            OneUiTheme.FunctionalGreen);

        bentoRow1.AddView(tileHrtf.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        bentoRow1.AddView(tileDist.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bentoRow2.AddView(tile3DOnly.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        bentoRow2.AddView(tileHeight.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        quickCard.AddView(bentoRow1);
        quickCard.AddView(bentoRow2);

        refreshBentoTilesUi = () => {
            tileHrtf.refresh();
            tileDist.refresh();
            tile3DOnly.refresh();
            tileHeight.refresh();
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
        LinearLayout mixerCard = Card(S["MixerTitle"], OneUiIconType.Queue, OneUiTheme.CoralStart);

        HorizontalScrollView filterScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout filterRow = HRow();
        filterAllBtn = FilterBtn(S["FilterAll"], ClearAllSoloFilters);
        filter3DBtn = FilterBtn(S["Filter3DOnly"], () => ToggleCategorySolo(item => item.IsDynamicObject, flag => {
            store.MuteBed = flag;
            playerEngine.MuteBed = flag;
        }));
        filterHeightBtn = FilterBtn(S["FilterHeightOnly"], () => ToggleCategorySolo(item => item.NormalizedPosition.Y > 0.15f, flag => {
            store.MuteGround = flag;
            playerEngine.MuteGround = flag;
        }));
        filterBedsBtn = FilterBtn(S["FilterBedsOnly"], () => ToggleCategorySolo(item => item.IsBed, flag => {
            playerEngine.OnlyBeds = flag;
        }));
        Button clearSoloBtn = FilterBtn(S["ClearSoloMute"], () => {
            playerEngine.ClearSoloMute();
            store.MuteBed = false;
            store.MuteGround = false;
            playerEngine.MuteBed = false;
            playerEngine.MuteGround = false;
            playerEngine.OnlyBeds = false;
            RefreshPlayerSourcesList();
            RefreshFilterButtonsUi();
            refreshBentoTilesUi?.Invoke();
            RefreshSettingsTabButtons();
            UpdateRoomCanvas();
        });

        filterRow.AddView(filterAllBtn);
        filterRow.AddView(filter3DBtn);
        filterRow.AddView(filterHeightBtn);
        filterRow.AddView(filterBedsBtn);
        filterRow.AddView(clearSoloBtn);
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
            badge.UpdateColors(on ? activeColor : OneUiTheme.ControlStroke, ColText);
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
        foreach (TrackSourceItem item in trackSourceItems) {
            item.IsSolo = false;
        }
        store.MuteBed = false;
        store.MuteGround = false;
        playerEngine.MuteBed = false;
        playerEngine.MuteGround = false;
        playerEngine.OnlyBeds = false;
        RefreshPlayerSourcesList();
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
            }
        } else {
            int matchedCount = 0;
            for (int i = 0; i < trackSourceItems.Count; i++) {
                bool m = predicate(trackSourceItems[i]);
                trackSourceItems[i].IsSolo = m;
                if (m) {
                    matchedCount++;
                }
            }
            if (matchedCount == 0) {
                Toast.MakeText(this, "Nenhum item nesta categoria na faixa atual.", ToastLength.Short).Show();
            }
        }

        RefreshPlayerSourcesList();
        RefreshFilterButtonsUi();
        refreshBentoTilesUi?.Invoke();
        RefreshSettingsTabButtons();
        UpdateRoomCanvas();
    }

    void RefreshFilterButtonsUi() {
        bool solo3D = IsCategorySoloActive(item => item.IsDynamicObject);
        bool soloH = IsCategorySoloActive(item => item.NormalizedPosition.Y > 0.15f);
        bool soloBeds = IsCategorySoloActive(item => item.IsBed);
        bool noneActive = !solo3D && !soloH && !soloBeds && !trackSourceItems.Any(x => x.IsSolo);

        void Style(Button b, bool active, Color accent) {
            if (b == null) return;
            b.Background = active
                ? OneUiTheme.CreateRippleRoundedDrawable(this, accent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        Style(filterAllBtn, noneActive, ColAccent);
        Style(filter3DBtn, solo3D, OneUiTheme.CoralStart);
        Style(filterHeightBtn, soloH, OneUiTheme.FunctionalGreen);
        Style(filterBedsBtn, soloBeds, OneUiTheme.Primary);
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
        SyncPlaybackUi(playerEngine.IsPlaying, target, playerEngine.TotalSeconds);
        mediaNotification?.UpdatePlaybackState(playerEngine.IsPlaying, target, showNotification: true);
    }

    void OnPlayerCompleted() {
        SyncPlaybackUi(false, playerEngine?.TotalSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
        mediaNotification?.UpdatePlaybackState(false, playerEngine?.TotalSeconds ?? 0, showNotification: selectedTrack != null);
    }

    void SyncPlaybackUi(bool isPlaying, double curSeconds, double totSeconds) {
        if (playPauseBtn != null) {
            playPauseBtn.Text = isPlaying ? S["Pause"] : S["Play"];
        }
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
            roomTelemetryText.Text = $"X: {p.X:0.0}m  •  Y: {p.Y:0.0}m  •  Z: {p.Z:0.0}m  •  Yaw: {r.Y:0}°  •  Pitch: {r.X:0}°";
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
    }

    void RefreshPlayerSourcesList() {
        if (mixerContainer == null) {
            return;
        }
        mixerContainer.RemoveAllViews();
        if (trackSourceItems.Count == 0) {
            mixerContainer.AddView(Label(S["NoTrack"], 12f, ColMuted));
            return;
        }

        foreach (TrackSourceItem item in trackSourceItems) {
            LinearLayout row = HRow();
            row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f, item.IsSolo ? OneUiTheme.SoloGold : null);
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
            void UpdateBtns() {
                soloBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                    this, item.IsSolo ? OneUiTheme.SoloGold : OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
                soloBtn.SetTextColor(item.IsSolo ? Color.Black : ColText);
                soloBtn.SetTypeface(null, item.IsSolo ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                muteBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                    this, item.IsMuted ? ColDanger : OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
                muteBtn.SetTextColor(ColText);
                row.Background = OneUiTheme.CreateGlassCardDrawable(
                    this, OneUiTheme.SurfaceSubTile, 18f, item.IsSolo ? OneUiTheme.SoloGold : null);
            }
            soloBtn.Click += (_, _) => {
                item.IsSolo = !item.IsSolo;
                UpdateBtns();
                RefreshFilterButtonsUi();
                refreshBentoTilesUi?.Invoke();
                UpdateRoomCanvas();
            };
            muteBtn.Click += (_, _) => {
                item.IsMuted = !item.IsMuted;
                UpdateBtns();
                UpdateRoomCanvas();
            };
            UpdateBtns();

            row.AddView(soloBtn, new LinearLayout.LayoutParams(Dp(38), ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
            row.AddView(muteBtn, new LinearLayout.LayoutParams(Dp(38), ViewGroup.LayoutParams.WrapContent));
            mixerContainer.AddView(row, rlp);
        }
    }

    void StartTelemetryTimer() {
        void Tick() {
            if (uiDestroyed) {
                return;
            }
            if (playerEngine != null && playerEngine.IsPlaying) {
                SyncPlaybackUi(true, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
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
        if (string.IsNullOrEmpty(helpBody)) {
            Switch sw = new(this) {
                Text = label,
                Checked = initial,
                TextSize = 13.5f
            };
            OneUiTheme.StyleSwitch(sw);
            sw.SetPadding(0, Dp(8), 0, Dp(8));
            sw.CheckedChange += (_, e) => onChanged?.Invoke(e.IsChecked);
            parent.AddView(sw);
            return sw;
        }

        LinearLayout row = HRow();
        row.SetPadding(0, Dp(6), 0, Dp(6));
        TextView lbl = Label(label, 13.5f, ColText);
        View helpBtn = CreateInlineHelpBtn(label, helpBody);
        Switch swCompact = new(this) {
            Text = "",
            Checked = initial
        };
        OneUiTheme.StyleSwitch(swCompact);
        swCompact.CheckedChange += (_, e) => onChanged?.Invoke(e.IsChecked);
        lbl.Click += (_, _) => swCompact.Checked = !swCompact.Checked;

        row.AddView(lbl, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(helpBtn, new LinearLayout.LayoutParams(Dp(26), Dp(26)) { LeftMargin = Dp(6), RightMargin = Dp(8) });
        row.AddView(swCompact, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        parent.AddView(row);
        return swCompact;
    }
}
