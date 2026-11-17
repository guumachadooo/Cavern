using System.Numerics;

using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Utilities;

using Cavernize.Logic.Models;
using CavernizeAndroid.Views;

namespace CavernizeAndroid;

public partial class MainActivity {
    RoomSimulatorView roomView;
    SeekBar seekBarPosX, seekBarPosZ, seekBarPosY, seekBarYaw, seekBarPitch;
    SeekBar playbackSeekBar, seekBarVolume, seekBarLfe;
    TextView timeDisplay, roomTelemetryText;
    Button playPauseBtn;
    Switch headphoneVirtSwitch, distSimSwitch, loopSwitch;
    Spinner rolloffSpinner;
    LinearLayout mixerContainer;
    bool isUserSeeking;

    LinearLayout BuildPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(10), Dp(10), Dp(10), Dp(16));

        // 1. Room Simulator Card
        LinearLayout roomCard = Card(S["RoomTitle"]);
        roomView = new RoomSimulatorView(this);
        roomView.ListenerPositionChanged += pos => {
            playerEngine.ListenerPosition = new Vector3(pos.X, playerEngine.ListenerPosition.Y, pos.Z);
            SyncRoomSlidersFromEngine();
        };
        roomCard.AddView(roomView);

        // Seat Presets Row
        HorizontalScrollView presetScroll = new(this);
        LinearLayout presetRow = new(this) { Orientation = Orientation.Horizontal };
        presetRow.SetPadding(0, Dp(6), 0, Dp(4));
        (string label, Func<Vector3, Vector3> posFn)[] presets = [
            (S["PresetCenter"], e => Vector3.Zero),
            (S["PresetFront"], e => new Vector3(0, 0, e.Z * 0.65f)),
            (S["PresetBack"], e => new Vector3(0, 0, -e.Z * 0.65f)),
            (S["PresetLeft"], e => new Vector3(-e.X * 0.65f, 0, 0)),
            (S["PresetRight"], e => new Vector3(e.X * 0.65f, 0, 0)),
            (S["PresetCeiling"], e => new Vector3(0, e.Y * 0.65f, 0)),
        ];
        foreach (var (label, posFn) in presets) {
            Button pb = SecondaryButton(label, () => {
                playerEngine.ListenerPosition = posFn(playerEngine.EnvironmentSize);
                if (label == S["PresetCenter"]) {
                    playerEngine.ListenerRotation = Vector3.Zero;
                }
                SyncRoomSlidersFromEngine();
                UpdateRoomCanvas();
            });
            pb.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            presetRow.AddView(pb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)) { RightMargin = Dp(6) });
        }
        presetScroll.AddView(presetRow);
        roomCard.AddView(presetScroll);

        roomTelemetryText = Label("", 11.5f, ColCyan);
        roomCard.AddView(roomTelemetryText);

        // Room Coordinate & Rotation Sliders
        (seekBarPosX, _) = AddSliderRow(roomCard, S["PosX"], 0, 200, 100, v => {
            float nx = (v - 100) / 100f;
            playerEngine.ListenerPosition = new Vector3(nx * playerEngine.EnvironmentSize.X, playerEngine.ListenerPosition.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        });
        (seekBarPosZ, _) = AddSliderRow(roomCard, S["PosZ"], 0, 200, 100, v => {
            float nz = (v - 100) / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, playerEngine.ListenerPosition.Y, nz * playerEngine.EnvironmentSize.Z);
            UpdateRoomCanvas();
        });
        (seekBarPosY, _) = AddSliderRow(roomCard, S["PosY"], 0, 200, 100, v => {
            float ny = (v - 100) / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, ny * playerEngine.EnvironmentSize.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        });
        (seekBarYaw, _) = AddSliderRow(roomCard, S["RotYaw"], 0, 360, 180, v => {
            float yaw = v - 180f;
            playerEngine.ListenerRotation = new Vector3(playerEngine.ListenerRotation.X, yaw, 0);
            UpdateRoomCanvas();
        });
        (seekBarPitch, _) = AddSliderRow(roomCard, S["RotPitch"], 0, 180, 90, v => {
            float pitch = v - 90f;
            playerEngine.ListenerRotation = new Vector3(pitch, playerEngine.ListenerRotation.Y, 0);
            UpdateRoomCanvas();
        });

        headphoneVirtSwitch = AddSwitchRow(roomCard, S["HeadphoneVirt"], store.HeadphoneVirtualizer, val => {
            store.HeadphoneVirtualizer = val;
            playerEngine.HeadphoneVirtualizer = val;
        });
        distSimSwitch = AddSwitchRow(roomCard, S["DistSim"], store.DistanceSimulation, val => {
            store.DistanceSimulation = val;
            playerEngine.DistanceSimulation = val;
        });

        LinearLayout rolloffRow = new(this) { Orientation = Orientation.Horizontal };
        rolloffRow.SetGravity(GravityFlags.CenterVertical);
        rolloffRow.AddView(Label(S["RolloffLabel"], 12.5f, ColMuted), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
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
        roomCard.AddView(rolloffRow);
        page.AddView(roomCard);

        // 2. Transport Controls Card
        LinearLayout transportCard = Card();
        playbackSeekBar = new SeekBar(this) { Max = 1000 };
        playbackSeekBar.StartTrackingTouch += (_, _) => isUserSeeking = true;
        playbackSeekBar.StopTrackingTouch += (_, _) => {
            isUserSeeking = false;
            if (playerEngine.TotalSeconds > 0) {
                playerEngine.Seek(playbackSeekBar.Progress / 1000.0 * playerEngine.TotalSeconds);
            }
        };
        transportCard.AddView(playbackSeekBar);

        timeDisplay = Label("00:00 / 00:00", 12f, ColMuted);
        timeDisplay.Gravity = GravityFlags.End;
        transportCard.AddView(timeDisplay);

        LinearLayout btnRow = new(this) { Orientation = Orientation.Horizontal };
        btnRow.SetPadding(0, Dp(6), 0, Dp(6));
        Button back10 = SecondaryButton("-10s", () => SeekRelative(-10));
        playPauseBtn = PrimaryButton(S["Play"], TogglePlayPause);
        Button stopBtn = SecondaryButton(S["Stop"], StopPlayback);
        Button fwd10 = SecondaryButton("+10s", () => SeekRelative(10));
        btnRow.AddView(back10, new LinearLayout.LayoutParams(0, Dp(42), 1f) { RightMargin = Dp(4) });
        btnRow.AddView(playPauseBtn, new LinearLayout.LayoutParams(0, Dp(42), 1.4f) { RightMargin = Dp(4) });
        btnRow.AddView(stopBtn, new LinearLayout.LayoutParams(0, Dp(42), 1f) { RightMargin = Dp(4) });
        btnRow.AddView(fwd10, new LinearLayout.LayoutParams(0, Dp(42), 1f));
        transportCard.AddView(btnRow);

        loopSwitch = AddSwitchRow(transportCard, S["Loop"], false, val => playerEngine.Loop = val);
        (seekBarVolume, _) = AddSliderRow(transportCard, S["MasterVol"], 0, 200, (int)(store.PlayerVolume * 100), v => {
            float vol = v / 100f;
            store.PlayerVolume = vol;
            playerEngine.Volume = vol;
        });
        (seekBarLfe, _) = AddSliderRow(transportCard, S["LfeVol"], 0, 200, (int)(store.LfeVolume * 100), v => {
            float vol = v / 100f;
            store.LfeVolume = vol;
            playerEngine.LFEVolume = vol;
        });
        page.AddView(transportCard);

        // 3. Live Channel & 3D Object Mixer Card
        LinearLayout mixerCard = Card(S["MixerTitle"]);

        HorizontalScrollView filterScroll = new(this);
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
        mixerContainer.SetPadding(0, Dp(6), 0, 0);
        mixerCard.AddView(mixerContainer);
        page.AddView(mixerCard);

        return page;
    }

    Button FilterBtn(string text, Action onClick) {
        Button b = SecondaryButton(text, onClick);
        b.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        b.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)) { RightMargin = Dp(6) };
        return b;
    }

    void ApplyQuickFilter(bool muteBed, bool muteGround, bool onlyBeds) {
        playerEngine.MuteBed = muteBed;
        playerEngine.MuteGround = muteGround;
        playerEngine.OnlyBeds = onlyBeds;
        store.MuteBed = muteBed;
        store.MuteGround = muteGround;
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
        }
    }

    void SeekRelative(double deltaSeconds) {
        if (selectedTrack == null || playerEngine.TotalSeconds <= 0) {
            return;
        }
        double target = Math.Clamp(playerEngine.CurrentSeconds + deltaSeconds, 0, Math.Max(0, playerEngine.TotalSeconds - 0.1));
        playerEngine.Seek(target);
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
        if (seekBarPosX != null) seekBarPosX.Progress = Math.Clamp((int)Math.Round((pos.X / env.X + 1f) * 100f), 0, 200);
        if (seekBarPosZ != null) seekBarPosZ.Progress = Math.Clamp((int)Math.Round((pos.Z / env.Z + 1f) * 100f), 0, 200);
        if (seekBarPosY != null) seekBarPosY.Progress = Math.Clamp((int)Math.Round((pos.Y / env.Y + 1f) * 100f), 0, 200);
        if (seekBarYaw != null) seekBarYaw.Progress = Math.Clamp((int)Math.Round(rot.Y + 180f), 0, 360);
        if (seekBarPitch != null) seekBarPitch.Progress = Math.Clamp((int)Math.Round(rot.X + 90f), 0, 180);
    }

    void UpdateRoomCanvas() {
        if (roomView == null || playerEngine == null) {
            return;
        }
        roomView.UpdateState(trackSourceItems, playerEngine.EnvironmentSize, playerEngine.ListenerPosition, playerEngine.ListenerRotation);
        if (roomTelemetryText != null) {
            Vector3 p = playerEngine.ListenerPosition;
            Vector3 r = playerEngine.ListenerRotation;
            roomTelemetryText.Text = $"X: {p.X:0.0}m | Y: {p.Y:0.0}m | Z: {p.Z:0.0}m | Yaw: {r.Y:0}° | Pitch: {r.X:0}°";
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
            row.SetBackgroundColor(ColCardAlt);
            row.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
            LinearLayout.LayoutParams rlp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(4)
            };

            LinearLayout infoCol = new(this) { Orientation = Orientation.Vertical };
            TextView nameTv = Label(item.Name, 12.5f, item.IsDynamicObject ? Color.ParseColor("#F472B6") : ColCyan);
            TextView subTv = Label($"Pos: ({item.NormalizedPosition.X:0.0}, {item.NormalizedPosition.Y:0.0}, {item.NormalizedPosition.Z:0.0})", 10.5f, ColMuted);
            infoCol.AddView(nameTv);
            infoCol.AddView(subTv);
            row.AddView(infoCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            Button soloBtn = new(this) { Text = "S", TextSize = 11.5f };
            Button muteBtn = new(this) { Text = "M", TextSize = 11.5f };
            void UpdateBtns() {
                soloBtn.SetBackgroundColor(item.IsSolo ? Color.ParseColor("#FACC15") : ColCard);
                soloBtn.SetTextColor(item.IsSolo ? Color.Black : ColText);
                muteBtn.SetBackgroundColor(item.IsMuted ? ColDanger : ColCard);
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

            row.AddView(soloBtn, new LinearLayout.LayoutParams(Dp(38), Dp(34)) { RightMargin = Dp(4) });
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
                }
                if (timeDisplay != null) {
                    timeDisplay.Text = $"{TimeSpan.FromSeconds(cur):mm\\:ss} / {TimeSpan.FromSeconds(tot):mm\\:ss}";
                }
                UpdateRoomCanvas();
            }
            uiHandler?.PostDelayed(Tick, 65);
        }
        uiHandler?.PostDelayed(Tick, 65);
    }

    (SeekBar bar, TextView valLabel) AddSliderRow(LinearLayout parent, string label, int min, int max, int initial, Action<int> onChanged) {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        box.SetPadding(0, Dp(2), 0, Dp(2));
        LinearLayout top = new(this) { Orientation = Orientation.Horizontal };
        TextView nameTv = Label(label, 12f, ColMuted);
        TextView valTv = Label(initial.ToString(), 12f, ColText);
        top.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(valTv);
        box.AddView(top);

        SeekBar bar = new(this) { Max = max - min, Progress = Math.Clamp(initial - min, 0, max - min) };
        bar.ProgressChanged += (_, e) => {
            int val = e.Progress + min;
            valTv.Text = val.ToString();
            if (e.FromUser) {
                onChanged?.Invoke(val);
            }
        };
        box.AddView(bar);
        parent.AddView(box);
        return (bar, valTv);
    }

    Switch AddSwitchRow(LinearLayout parent, string label, bool initial, Action<bool> onChanged) {
        Switch sw = new(this) {
            Text = label,
            Checked = initial,
            TextSize = 13f
        };
        sw.SetTextColor(ColText);
        sw.SetPadding(0, Dp(6), 0, Dp(6));
        sw.CheckedChange += (_, e) => onChanged?.Invoke(e.IsChecked);
        parent.AddView(sw);
        return sw;
    }
}
