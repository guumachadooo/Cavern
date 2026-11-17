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
    Button roomLockBtn, fineTuneToggleBtn;
    LinearLayout fineTuneContainer;
    bool isFineTuneExpanded;
    SeekBar seekBarPosX, seekBarPosZ, seekBarPosY, seekBarYaw, seekBarPitch;
    OneUiSeekBar playbackSeekBar;
    SeekBar seekBarVolume, seekBarLfe;
    TextView roomTelemetryText;
    Button playPauseBtn;
    Switch loopSwitch;
    Spinner rolloffSpinner;
    LinearLayout mixerContainer;
    bool isUserSeeking;

    Action refreshBentoTilesUi;

    LinearLayout BuildPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(4), Dp(14), Dp(88));

        // 1. 3D Room Simulator Card (One UI 9 Frosted Surface)
        LinearLayout roomCard = Card();

        LinearLayout roomHeader = new(this) { Orientation = Orientation.Horizontal };
        roomHeader.SetGravity(GravityFlags.CenterVertical);
        roomHeader.SetPadding(0, 0, 0, Dp(6));
        roomHeader.AddView(OneUiTheme.CreateIconBadge(this, "◎", OneUiTheme.Primary, 30));
        TextView roomTitleTv = SectionTitle(S["RoomTitle"]);
        roomTitleTv.SetPadding(Dp(8), 0, Dp(8), 0);
        roomHeader.AddView(roomTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        roomLockBtn = PillButton("", () => {
            store.RoomPositionLocked = !store.RoomPositionLocked;
            if (roomView != null) {
                roomView.IsPositionLocked = store.RoomPositionLocked;
                roomView.Invalidate();
            }
            UpdateRoomLockButtonUi();
        });
        roomLockBtn.TextSize = 11.5f;
        roomLockBtn.SetPadding(Dp(12), Dp(2), Dp(12), Dp(2));
        roomHeader.AddView(roomLockBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)));
        roomCard.AddView(roomHeader);

        TextView hintTv = Label(S["RoomTouchHint"], 11.5f, OneUiTheme.TextMuted);
        hintTv.SetPadding(0, 0, 0, Dp(6));
        roomCard.AddView(hintTv);

        roomView = new RoomSimulatorView(this) {
            IsPositionLocked = store.RoomPositionLocked
        };
        UpdateRoomLockButtonUi();
        roomView.ListenerPositionChanged += pos => {
            playerEngine.ListenerPosition = new Vector3(pos.X, playerEngine.ListenerPosition.Y, pos.Z);
            SyncRoomSlidersFromEngine();
        };
        roomCard.AddView(roomView);

        // Seat Presets Pill Row
        HorizontalScrollView presetScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout presetRow = new(this) { Orientation = Orientation.Horizontal };
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
            pb.SetPadding(Dp(14), Dp(2), Dp(14), Dp(2));
            presetRow.AddView(pb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)) { RightMargin = Dp(6) });
        }
        presetScroll.AddView(presetRow);
        roomCard.AddView(presetScroll);

        roomTelemetryText = Label("", 11.5f, OneUiTheme.PeachText);
        roomTelemetryText.SetPadding(0, 0, 0, Dp(8));
        roomCard.AddView(roomTelemetryText);

        // Collapsible Fine-Tune Capsule Sliders
        fineTuneToggleBtn = PillButton(isFineTuneExpanded ? S["FineTuneHideBtn"] : S["FineTuneBtn"], () => {
            isFineTuneExpanded = !isFineTuneExpanded;
            fineTuneContainer.Visibility = isFineTuneExpanded ? ViewStates.Visible : ViewStates.Gone;
            fineTuneToggleBtn.Text = isFineTuneExpanded ? S["FineTuneHideBtn"] : S["FineTuneBtn"];
        });
        roomCard.AddView(fineTuneToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(38)));

        fineTuneContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isFineTuneExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        fineTuneContainer.SetPadding(0, Dp(10), 0, 0);
        (seekBarPosX, _) = AddSliderRow(fineTuneContainer, S["PosX"], -100, 100, 0, v => {
            float nx = v / 100f;
            playerEngine.ListenerPosition = new Vector3(nx * playerEngine.EnvironmentSize.X, playerEngine.ListenerPosition.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true);
        (seekBarPosZ, _) = AddSliderRow(fineTuneContainer, S["PosZ"], -100, 100, 0, v => {
            float nz = v / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, playerEngine.ListenerPosition.Y, nz * playerEngine.EnvironmentSize.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true);
        (seekBarPosY, _) = AddSliderRow(fineTuneContainer, S["PosY"], -100, 100, 0, v => {
            float ny = v / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, ny * playerEngine.EnvironmentSize.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true);
        (seekBarYaw, _) = AddSliderRow(fineTuneContainer, S["RotYaw"], -180, 180, 0, v => {
            playerEngine.ListenerRotation = new Vector3(playerEngine.ListenerRotation.X, v, 0);
            UpdateRoomCanvas();
        }, "°");
        (seekBarPitch, _) = AddSliderRow(fineTuneContainer, S["RotPitch"], -90, 90, 0, v => {
            playerEngine.ListenerRotation = new Vector3(v, playerEngine.ListenerRotation.Y, 0);
            UpdateRoomCanvas();
        }, "°");
        roomCard.AddView(fineTuneContainer);
        page.AddView(roomCard);

        // 2. Voice Recorder-style Transport & Capsule Timeline Card
        LinearLayout transportCard = Card();
        playbackSeekBar = new OneUiSeekBar(this) {
            Max = 1000,
            LabelText = S["TimelineLabel"],
            ValueText = "00:00 / 00:00",
            UseBlueAccent = true
        };
        playbackSeekBar.StartTrackingTouch += (_, _) => isUserSeeking = true;
        playbackSeekBar.ProgressChanged += (_, e) => {
            if (e.FromUser && playerEngine.TotalSeconds > 0) {
                double preview = e.Progress / 1000.0 * playerEngine.TotalSeconds;
                playbackSeekBar.ValueText = $"{TimeSpan.FromSeconds(preview):mm\\:ss} / {TimeSpan.FromSeconds(playerEngine.TotalSeconds):mm\\:ss}";
                playbackSeekBar.Invalidate();
            }
        };
        playbackSeekBar.StopTrackingTouch += (_, _) => {
            isUserSeeking = false;
            if (playerEngine.TotalSeconds > 0) {
                playerEngine.Seek(playbackSeekBar.Progress / 1000.0 * playerEngine.TotalSeconds);
            }
        };
        transportCard.AddView(playbackSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });

        LinearLayout btnRow = new(this) { Orientation = Orientation.Horizontal };
        btnRow.SetGravity(GravityFlags.CenterVertical);
        Button back10 = PillButton("-10s", () => SeekRelative(-10));
        playPauseBtn = PrimaryButton(S["Play"], TogglePlayPause);
        Button stopBtn = PillButton(S["Stop"], StopPlayback);
        Button fwd10 = PillButton("+10s", () => SeekRelative(10));
        btnRow.AddView(back10, new LinearLayout.LayoutParams(0, Dp(44), 1f) { RightMargin = Dp(6) });
        btnRow.AddView(playPauseBtn, new LinearLayout.LayoutParams(0, Dp(46), 1.45f) { RightMargin = Dp(6) });
        btnRow.AddView(stopBtn, new LinearLayout.LayoutParams(0, Dp(44), 1f) { RightMargin = Dp(6) });
        btnRow.AddView(fwd10, new LinearLayout.LayoutParams(0, Dp(44), 1f));
        transportCard.AddView(btnRow);

        AddDivider(transportCard);
        loopSwitch = AddSwitchRow(transportCard, S["Loop"], false, val => playerEngine.Loop = val);
        page.AddView(transportCard);

        // 3. One UI 9 Quick Spatial Panel Card (2x2 Bento Toggles + Fat Coral Volume Capsules)
        LinearLayout quickCard = Card(S["QuickPanelTitle"], "🎛", OneUiTheme.FunctionalIndigo);

        LinearLayout bentoRow1 = new(this) { Orientation = Orientation.Horizontal };
        LinearLayout bentoRow2 = new(this) { Orientation = Orientation.Horizontal };
        bentoRow1.SetPadding(0, 0, 0, Dp(8));
        bentoRow2.SetPadding(0, 0, 0, Dp(10));

        var tileHrtf = CreateBentoToggle(
            "🎧", S["BentoHrtf"],
            () => store.HeadphoneVirtualizer,
            () => {
                bool next = !store.HeadphoneVirtualizer;
                store.HeadphoneVirtualizer = next;
                playerEngine.HeadphoneVirtualizer = next;
            },
            OneUiTheme.Primary);

        var tileDist = CreateBentoToggle(
            "📏", S["BentoDist"],
            () => store.DistanceSimulation,
            () => {
                bool next = !store.DistanceSimulation;
                store.DistanceSimulation = next;
                playerEngine.DistanceSimulation = next;
            },
            OneUiTheme.FunctionalCyan);

        var tile3DOnly = CreateBentoToggle(
            "✦", S["Bento3DOnly"],
            () => store.MuteBed,
            () => ApplyQuickFilter(muteBed: !store.MuteBed, muteGround: false, onlyBeds: false),
            OneUiTheme.CoralStart);

        var tileHeight = CreateBentoToggle(
            "↑", S["BentoHeightOnly"],
            () => store.MuteGround,
            () => ApplyQuickFilter(muteBed: false, muteGround: !store.MuteGround, onlyBeds: false),
            OneUiTheme.FunctionalGreen);

        bentoRow1.AddView(tileHrtf.view, new LinearLayout.LayoutParams(0, Dp(62), 1f) { RightMargin = Dp(8) });
        bentoRow1.AddView(tileDist.view, new LinearLayout.LayoutParams(0, Dp(62), 1f));
        bentoRow2.AddView(tile3DOnly.view, new LinearLayout.LayoutParams(0, Dp(62), 1f) { RightMargin = Dp(8) });
        bentoRow2.AddView(tileHeight.view, new LinearLayout.LayoutParams(0, Dp(62), 1f));

        quickCard.AddView(bentoRow1);
        quickCard.AddView(bentoRow2);

        refreshBentoTilesUi = () => {
            tileHrtf.refresh();
            tileDist.refresh();
            tile3DOnly.refresh();
            tileHeight.refresh();
        };
        refreshBentoTilesUi();

        (seekBarVolume, _) = AddSliderRow(quickCard, S["MasterVol"], 0, 200, (int)Math.Round(store.PlayerVolume * 100), v => {
            float vol = v / 100f;
            store.PlayerVolume = vol;
            playerEngine.Volume = vol;
        }, "%");

        (seekBarLfe, _) = AddSliderRow(quickCard, S["LfeVol"], 0, 200, (int)Math.Round(store.LfeVolume * 100), v => {
            float vol = v / 100f;
            store.LfeVolume = vol;
            playerEngine.LFEVolume = vol;
        }, "%");

        AddDivider(quickCard);
        LinearLayout rolloffRow = new(this) { Orientation = Orientation.Horizontal };
        rolloffRow.SetGravity(GravityFlags.CenterVertical);
        rolloffRow.AddView(Label(S["RolloffLabel"], 12.5f, OneUiTheme.TextSub), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
        rolloffSpinner = new Spinner(this);
        string[] rolloffNames = ["Disabled", "Logarithmic", "Linear", "Real (1/d)"];
        ArrayAdapter<string> rolloffAdapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, rolloffNames);
        rolloffAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        rolloffSpinner.Adapter = rolloffAdapter;
        rolloffSpinner.SetSelection(Math.Clamp(store.VolumeRolloffIndex, 0, 3));
        rolloffSpinner.ItemSelected += (_, e) => {
            store.VolumeRolloffIndex = e.Position;
            playerEngine.VolumeRolloff = (Rolloffs)e.Position;
        };
        rolloffRow.AddView(rolloffSpinner, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        quickCard.AddView(rolloffRow);
        page.AddView(quickCard);

        // 4. Live Channel & 3D Object Mixer Card
        LinearLayout mixerCard = Card(S["MixerTitle"], "🎚", OneUiTheme.CoralStart);

        HorizontalScrollView filterScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout filterRow = new(this) { Orientation = Orientation.Horizontal };
        filterRow.AddView(FilterBtn(S["FilterAll"], () => ApplyQuickFilter(muteBed: false, muteGround: false, onlyBeds: false)));
        filterRow.AddView(FilterBtn(S["Filter3DOnly"], () => ApplyQuickFilter(muteBed: true, muteGround: false, onlyBeds: false)));
        filterRow.AddView(FilterBtn(S["FilterHeightOnly"], () => ApplyQuickFilter(muteBed: false, muteGround: true, onlyBeds: false)));
        filterRow.AddView(FilterBtn(S["FilterBedsOnly"], () => ApplyQuickFilter(muteBed: false, muteGround: false, onlyBeds: true)));
        filterRow.AddView(FilterBtn(S["ClearSoloMute"], () => {
            playerEngine.ClearSoloMute();
            ApplyQuickFilter(false, false, false);
            RefreshPlayerSourcesList();
        }));
        filterScroll.AddView(filterRow);
        mixerCard.AddView(filterScroll);

        mixerContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        mixerContainer.SetPadding(0, Dp(8), 0, 0);
        mixerCard.AddView(mixerContainer);
        page.AddView(mixerCard);

        return page;
    }

    (LinearLayout view, Action refresh) CreateBentoToggle(
        string glyph,
        string title,
        Func<bool> getState,
        Action onToggle,
        Color activeColor) {
        LinearLayout tile = new(this) { Orientation = Orientation.Horizontal };
        tile.SetGravity(GravityFlags.CenterVertical);
        tile.SetPadding(Dp(12), Dp(8), Dp(10), Dp(8));
        OneUiTheme.AttachRecoil(tile, 0.95f);

        TextView badge = OneUiTheme.CreateIconBadge(this, glyph, activeColor, 34);
        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = Label(title, 13f, ColText);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);
        TextView stateTv = Label("", 11f, OneUiTheme.TextSub);
        col.AddView(titleTv);
        col.AddView(stateTv);

        tile.AddView(badge, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(10) });
        tile.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

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
            badge.Background = OneUiTheme.CreateRoundedDrawable(
                this,
                on ? activeColor : OneUiTheme.ControlStroke,
                OneUiTheme.PillCornerDp);
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
        b.SetPadding(Dp(12), Dp(2), Dp(12), Dp(2));
        b.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)) { RightMargin = Dp(6) };
        return b;
    }

    void UpdateRoomLockButtonUi() {
        if (roomLockBtn == null) {
            return;
        }
        bool locked = store.RoomPositionLocked;
        roomLockBtn.Text = locked ? $"🔒 {S["UnlockRoom"]}" : $"↔ {S["LockRoom"]}";
        roomLockBtn.Background = locked
            ? OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.Primary, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        roomLockBtn.SetTextColor(OneUiTheme.TextPrimary);
    }

    void ApplyQuickFilter(bool muteBed, bool muteGround, bool onlyBeds) {
        playerEngine.MuteBed = muteBed;
        playerEngine.MuteGround = muteGround;
        playerEngine.OnlyBeds = onlyBeds;
        store.MuteBed = muteBed;
        store.MuteGround = muteGround;
        refreshBentoTilesUi?.Invoke();
        RefreshSettingsTabButtons();
        UpdateRoomCanvas();
    }

    void TogglePlayPause() {
        if (selectedTrack == null || !selectedTrack.Supported) {
            ShowError(S["NoTrack"]);
            return;
        }
        if (selectedTrack.Codec == Cavern.Format.Common.Codec.TrueHD) {
            ShowError(S["TrueHDUnsupported"]);
            return;
        }
        if (playerEngine.IsPlaying) {
            playerEngine.IsPlaying = false;
            audioOutput.Pause();
            playPauseBtn.Text = S["Play"];
        } else {
            if (playerEngine.TotalSeconds > 0 && playerEngine.CurrentSeconds >= playerEngine.TotalSeconds - 0.05) {
                playerEngine.Seek(0);
            }
            playerEngine.IsPlaying = true;
            audioOutput.Start();
            playPauseBtn.Text = S["Pause"];
        }
    }

    void StopPlayback() {
        if (playerEngine != null) {
            playerEngine.IsPlaying = false;
            playerEngine.Seek(0);
        }
        audioOutput?.Stop();
        if (playPauseBtn != null) {
            playPauseBtn.Text = S["Play"];
        }
        if (playbackSeekBar != null) {
            playbackSeekBar.Progress = 0;
            playbackSeekBar.ValueText = $"00:00 / {TimeSpan.FromSeconds(playerEngine?.TotalSeconds ?? 0):mm\\:ss}";
            playbackSeekBar.Invalidate();
        }
    }

    void SeekRelative(double deltaSeconds) {
        if (selectedTrack == null || playerEngine.TotalSeconds <= 0) {
            return;
        }
        double target = Math.Clamp(playerEngine.CurrentSeconds + deltaSeconds, 0, Math.Max(0, playerEngine.TotalSeconds - 0.1));
        playerEngine.Seek(target);
        if (playbackSeekBar != null) {
            playbackSeekBar.Progress = Math.Clamp((int)(target / playerEngine.TotalSeconds * 1000.0), 0, 1000);
            playbackSeekBar.ValueText = $"{TimeSpan.FromSeconds(target):mm\\:ss} / {TimeSpan.FromSeconds(playerEngine.TotalSeconds):mm\\:ss}";
            playbackSeekBar.Invalidate();
        }
    }

    void OnPlayerCompleted() {
        if (playPauseBtn != null) {
            playPauseBtn.Text = S["Play"];
        }
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

    void UpdateRoomCanvas() {
        if (roomView == null || playerEngine == null) {
            return;
        }
        roomView.UpdateState(trackSourceItems, playerEngine.EnvironmentSize, playerEngine.ListenerPosition, playerEngine.ListenerRotation);
        if (roomTelemetryText != null) {
            Vector3 p = playerEngine.ListenerPosition;
            Vector3 r = playerEngine.ListenerRotation;
            roomTelemetryText.Text = $"X: {p.X:0.0}m  •  Y: {p.Y:0.0}m  •  Z: {p.Z:0.0}m  •  Yaw: {r.Y:0}°  •  Pitch: {r.X:0}°";
        }
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
            LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 18f);
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
            soloBtn.SetPadding(0, 0, 0, 0);
            muteBtn.SetPadding(0, 0, 0, 0);
            OneUiTheme.AttachRecoil(soloBtn, 0.90f);
            OneUiTheme.AttachRecoil(muteBtn, 0.90f);
            void UpdateBtns() {
                soloBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                    this, item.IsSolo ? OneUiTheme.SoloGold : OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
                soloBtn.SetTextColor(item.IsSolo ? Color.Black : ColText);
                muteBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(
                    this, item.IsMuted ? ColDanger : OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
                muteBtn.SetTextColor(ColText);
            }
            soloBtn.Click += (_, _) => {
                item.IsSolo = !item.IsSolo;
                UpdateBtns();
                UpdateRoomCanvas();
            };
            muteBtn.Click += (_, _) => {
                item.IsMuted = !item.IsMuted;
                UpdateBtns();
                UpdateRoomCanvas();
            };
            UpdateBtns();

            row.AddView(soloBtn, new LinearLayout.LayoutParams(Dp(38), Dp(34)) { RightMargin = Dp(6) });
            row.AddView(muteBtn, new LinearLayout.LayoutParams(Dp(38), Dp(34)));
            mixerContainer.AddView(row, rlp);
        }
    }

    void StartTelemetryTimer() {
        void Tick() {
            if (uiDestroyed) {
                return;
            }
            if (playerEngine != null && playerEngine.IsPlaying) {
                double cur = playerEngine.CurrentSeconds;
                double tot = playerEngine.TotalSeconds;
                if (!isUserSeeking && playbackSeekBar != null && tot > 0) {
                    playbackSeekBar.Progress = Math.Clamp((int)(cur / tot * 1000.0), 0, 1000);
                    playbackSeekBar.ValueText = $"{TimeSpan.FromSeconds(cur):mm\\:ss} / {TimeSpan.FromSeconds(tot):mm\\:ss}";
                    playbackSeekBar.Invalidate();
                }
                UpdateRoomCanvas();
            }
            uiHandler?.PostDelayed(Tick, 65);
        }
        uiHandler?.PostDelayed(Tick, 65);
    }

    (SeekBar bar, TextView valLabel) AddSliderRow(
        LinearLayout parent,
        string label,
        int min,
        int max,
        int initial,
        Action<int> onChanged,
        string suffix = "",
        bool useBlue = false) {
        OneUiSeekBar bar = new(this) {
            Max = max - min,
            Progress = Math.Clamp(initial - min, 0, max - min),
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
        parent.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });
        return (bar, null);
    }

    Switch AddSwitchRow(LinearLayout parent, string label, bool initial, Action<bool> onChanged) {
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
}
