using System.Numerics;

using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.VocalChain;

using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;
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
    readonly List<Button> roomPresetBtns = [];
    Button roomResetAllBtn;
    Button signalChainDecoderPill, signalChainUpmixPill, signalChainLgsrPill, signalChainRoomHrtfPill, signalChainAutoEqLoudnessPill;
    LinearLayout roomMiniSeekDrawer;
    bool isMiniSeekExpanded;
    TextView roomMiniTimeText;
    Button roomMiniVolChip, roomMiniHrtfChip;
    LinearLayout mixerCardRef;
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
    Button filterAllBtn, filter3DBtn, filterStemsMicBtn, filterHeightBtn, filterBedsBtn;
    Button roomNeuralPrepBtn;
    TextView roomNeuralStatusText;
    bool isUserSeeking;

    // W13 Studio Spatial X-Ray Inspector HUD views
    LinearLayout xrayInspectorBody;
    Button xrayInspectorToggleBtn;
    Button xrayProTelemetryToggleBtn;
    LinearLayout xrayProTelemetryContainer;
    TextView xrayEssentialSummaryTv;
    bool isProTelemetryExpanded;
    TextView xraySoundfieldMetricsTv;
    TextView xrayLgsrMetricsTv;
    TextView xrayVocalMetricsTv;
    Button xrayLgsrBypassBtn;
    Button xrayVocalBypassBtn;
    Switch xrayOverlaySwitch;
    Button[] xrayBandBtns;

    Action refreshBentoTilesUi;

    LinearLayout BuildPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(14), Dp(6), Dp(14), Dp(116));

        // 0. Active File Source Bar (Clean workspace context & track selector)
        page.AddView(CreateWorkspaceSourceBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        // 0B. Live Tappable Active Signal Chain Bar ([Decoder/Format] -> [Upmix / AI 3D Stems] -> [LGSR / Twin] -> [Room / HRTF] -> [AutoEq / Loudness])
        page.AddView(BuildActiveSignalChainBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 1. 3D Room Simulator Card (One UI 9 Frosted Surface)
        LinearLayout roomCard = Card();

        LinearLayout roomHeader = HRow();
        roomHeader.SetPadding(0, 0, 0, Dp(6));
        roomHeader.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Room3D, OneUiTheme.IconSpatial, 30));
        TextView roomTitleTv = SectionTitle(S["RoomTitle"]);
        roomTitleTv.SetSingleLine(true);
        roomTitleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomTitleTv.SetPadding(Dp(8), 0, Dp(8), 0);
        roomHeader.AddView(roomTitleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        roomLockBtn = HRow();
        roomLockBtn.SetPadding(Dp(12), Dp(7), Dp(12), Dp(7));
        OneUiTheme.AttachRecoil(roomLockBtn, 0.94f);
        roomLockIcon = new OneUiIconView(this, OneUiIconType.Unlock, ColText);
        roomLockText = Label("", 11.5f, ColText);
        roomLockText.SetIncludeFontPadding(false);
        roomLockText.SetSingleLine(true);
        roomLockText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
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

        SpatialXRayBandMode initialBandMode = (SpatialXRayBandMode)Math.Clamp(store.RoomXRayBandMode, 0, 3);
        if (playerEngine?.SceneHub != null) {
            playerEngine.SceneHub.ActiveXRayBandMode = initialBandMode;
        }
        roomView = new RoomSimulatorView(this) {
            IsPositionLocked = store.RoomPositionLocked,
            IsXRayEnabled = store.RoomXRayOverlayEnabled,
            XRayBandMode = initialBandMode
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
        roomView.SourcePositionDragged += (item, normPos) => {
            playerEngine.SetSourceCustomPosition(item, normPos);
            UpdateMixerRowsLive();
        };
        roomView.SourcePositionResetRequested += item => {
            playerEngine.ClearSourceCustomPosition(item);
            UpdateRoomCanvas();
        };
        roomView.XRayToggled += enabled => {
            store.RoomXRayOverlayEnabled = enabled;
            if (xrayOverlaySwitch != null && xrayOverlaySwitch.Checked != enabled) {
                xrayOverlaySwitch.Checked = enabled;
            }
            if (enabled) {
                EnsureXRayInspectorExpanded();
            }
            UpdateRoomCanvas();
        };
        roomView.XRayBandModeChanged += band => {
            store.RoomXRayBandMode = (int)band;
            playerEngine.SceneHub.ActiveXRayBandMode = band;
            RefreshXRayBandButtonsUi();
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
        roomPresetBtns.Clear();
        foreach (var (label, posFn) in presets) {
            Button pb = PillButton(label, () => {
                if (store.RoomPositionLocked) {
                    Toast.MakeText(this, S["RoomPositionLockedToast"], ToastLength.Short).Show();
                    return;
                }
                playerEngine.ListenerPosition = posFn(playerEngine.EnvironmentSize);
                if (label == S["PresetCenter"]) {
                    playerEngine.ListenerRotation = Vector3.Zero;
                }
                SyncRoomSlidersFromEngine();
                UpdateRoomCanvas();
            });
            pb.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
            pb.SetMinimumHeight(Dp(36));
            roomPresetBtns.Add(pb);
            presetRow.AddView(pb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        }
        presetScroll.AddView(presetRow);
        roomCard.AddView(presetScroll);

        roomTelemetryText = Label("", 11.5f, OneUiTheme.PeachText);
        roomTelemetryText.SetSingleLine(true);
        roomTelemetryText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
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
            if (store.RoomPositionLocked) {
                Toast.MakeText(this, S["RoomPositionLockedToast"], ToastLength.Short).Show();
                return;
            }
            playerEngine.ListenerPosition = Vector3.Zero;
            playerEngine.ListenerRotation = Vector3.Zero;
            playerEngine.ResetAllCustomPositions();
            headTrackingManager?.Recenter();
            SyncRoomSlidersFromEngine();
            UpdateRoomCanvas();
        });
        roomResetAllBtn = resetRoomBtn;
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
            if (store.RoomPositionLocked) {
                SyncRoomSlidersFromEngine();
                return;
            }
            float nx = v / 100f;
            playerEngine.ListenerPosition = new Vector3(nx * playerEngine.EnvironmentSize.X, playerEngine.ListenerPosition.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true, defaultValue: 0);
        (seekBarPosZ, _) = AddSliderRow(fineTuneContainer, S["PosZ"], -100, 100, 0, v => {
            if (store.RoomPositionLocked) {
                SyncRoomSlidersFromEngine();
                return;
            }
            float nz = v / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, playerEngine.ListenerPosition.Y, nz * playerEngine.EnvironmentSize.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true, defaultValue: 0);
        (seekBarPosY, _) = AddSliderRow(fineTuneContainer, S["PosY"], -100, 100, 0, v => {
            if (store.RoomPositionLocked) {
                SyncRoomSlidersFromEngine();
                return;
            }
            float ny = v / 100f;
            playerEngine.ListenerPosition = new Vector3(playerEngine.ListenerPosition.X, ny * playerEngine.EnvironmentSize.Y, playerEngine.ListenerPosition.Z);
            UpdateRoomCanvas();
        }, "%", useBlue: true, defaultValue: 0);
        (seekBarYaw, _) = AddSliderRow(fineTuneContainer, S["RotYaw"], -180, 180, 0, v => {
            if (store.RoomPositionLocked) {
                SyncRoomSlidersFromEngine();
                return;
            }
            playerEngine.ListenerRotation = new Vector3(playerEngine.ListenerRotation.X, v, 0);
            UpdateRoomCanvas();
        }, "°", defaultValue: 0);
        (seekBarPitch, _) = AddSliderRow(fineTuneContainer, S["RotPitch"], -90, 90, 0, v => {
            if (store.RoomPositionLocked) {
                SyncRoomSlidersFromEngine();
                return;
            }
            playerEngine.ListenerRotation = new Vector3(v, playerEngine.ListenerRotation.Y, 0);
            UpdateRoomCanvas();
        }, "°", defaultValue: 0);
        UpdateRoomLockButtonUi();
        roomCard.AddView(fineTuneContainer);
        page.AddView(roomCard);

        // 1B. Compact 1-Row Mini Transport & Master Strip (keeps 3D Room canvas above the fold without 5-card vertical bloat)
        page.AddView(BuildMiniTransportStrip());

        // 1C. W13 Studio Spatial X-Ray Inspector Card (Native One UI 9 Collapsible HUD with Essential / Pro Telemetry Disclosure)
        page.AddView(BuildSpatialXRayInspectorCard());

        // 3. One UI 9 Quick Spatial Panel Card (1x2 Bento Toggles + Fat Dynamic Capsules + In-App Rolloff Selector)
        LinearLayout quickCard = Card(S["QuickPanelTitle"], OneUiIconType.Sliders, OneUiTheme.IconAtmosUpmix);

        bentoRow1 = HRow();
        bentoRow1.SetPadding(0, 0, 0, Dp(10));

        var tileHrtf = CreateBentoToggle(
            OneUiIconType.Headphones, S["BentoHrtf"],
            () => store.HeadphoneVirtualizer,
            ToggleRoomHrtfVirtualizer,
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
            v => {
                SetMasterVolumePercent(v);
                RefreshRoomTransportButtons();
            }, "%", defaultValue: 100);

        (seekBarLfe, _) = AddSliderRow(quickCard, S["LfeVol"], 0, 200, (int)Math.Round(store.LfeVolume * 100), v => {
            float vol = v / 100f;
            store.LfeVolume = vol;
            playerEngine.LFEVolume = vol;
        }, "%", defaultValue: 100);

        AddDivider(quickCard);
        rolloffSelector = new OneUiInlineSelector(this, S["RolloffLabel"]);
        string[] rolloffNames = [S["RolloffDisabled"], S["RolloffLogarithmic"], S["RolloffLinear"], S["RolloffReal"]];
        rolloffSelector.SetItems(rolloffNames, Math.Clamp(store.VolumeRolloffIndex, 0, 3));
        rolloffSelector.SelectionChanged += pos => {
            store.VolumeRolloffIndex = pos;
            playerEngine.VolumeRolloff = (Rolloffs)pos;
        };
        quickCard.AddView(WrapControlWithHelp(rolloffSelector, S["RolloffLabel"].TrimEnd(':'), S["TipRolloff"]));
        page.AddView(quickCard);

        // 4. Live Channel & 3D Object Mixer Card (Filter buttons visibly toggle Solo state)
        LinearLayout mixerCard = Card(S["MixerTitle"], OneUiIconType.Queue, OneUiTheme.IconStudioExport);
        mixerCardRef = mixerCard;

        HorizontalScrollView filterScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout filterRow = HRow();
        filterAllBtn = FilterBtn(S["FilterAll"], ClearAllSoloFilters);
        filter3DBtn = FilterBtn(S["Filter3DOnly"], () => ToggleCategorySolo(item => item.IsDynamicObject, flag => {
            store.MuteBed = flag;
            playerEngine.MuteBed = flag;
        }));
        filterStemsMicBtn = FilterBtn(S["FilterStemsMicOnly"], () => ToggleCategorySolo(
            item => item.Layer == SourceLayer.NeuralStem || item.Layer == SourceLayer.RestoredResidual || item.Layer == SourceLayer.LiveMicrophone,
            _ => { }));
        filterHeightBtn = FilterBtn(S["FilterHeightOnly"], ToggleDynamicCeilingFilter);
        filterBedsBtn = FilterBtn(S["FilterBedsOnly"], () => ToggleCategorySolo(item => item.IsBed, flag => {
            playerEngine.OnlyBeds = flag;
        }));
        filterRow.AddView(filterAllBtn);
        filterRow.AddView(filter3DBtn);
        filterRow.AddView(filterStemsMicBtn);
        filterRow.AddView(filterHeightBtn);
        filterRow.AddView(filterBedsBtn);
        filterScroll.AddView(filterRow);
        mixerCard.AddView(filterScroll);

        // W18 Live Neural 3D Stems / Karaoke Prep bar (non-blocking background ONNX + Slot 7B cache)
        LinearLayout neuralPrepRow = HRow();
        neuralPrepRow.SetGravity(GravityFlags.CenterVertical);
        neuralPrepRow.SetPadding(0, Dp(8), 0, Dp(2));
        roomNeuralStatusText = Label("", 11.2f, OneUiTheme.TextSub);
        roomNeuralStatusText.SetSingleLine(true);
        roomNeuralStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        neuralPrepRow.AddView(roomNeuralStatusText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        roomNeuralPrepBtn = PillButton(S["NeuralPrep3DBtn"], () => {
            CavernizeTrack active = GetActiveTrack();
            if (active == null || !active.Supported) {
                if (roomNeuralStatusText != null) {
                    roomNeuralStatusText.Text = S["NeuralPrepNoTrackToast"];
                    roomNeuralStatusText.SetTextColor(OneUiTheme.FunctionalOrange);
                }
                Toast.MakeText(this, S["NeuralPrepNoTrackToast"], ToastLength.Short).Show();
                return;
            }
            if (!isNeuralPrepRunning && playerEngine?.NeuralStage?.HasPlayableSeparatedStems == true) {
                int stemCnt = playerEngine.NeuralStage.ActiveSeparatedTrack?.Stems?.Count ?? 0;
                Toast.MakeText(this, string.Format(S["NeuralPrepCacheHitToast"], stemCnt), ToastLength.Short).Show();
                ToggleCategorySolo(
                    item => item.Layer == SourceLayer.NeuralStem || item.Layer == SourceLayer.RestoredResidual || item.Layer == SourceLayer.LiveMicrophone,
                    _ => { });
                return;
            }
            PrepareActiveTrackNeuralStemsAsync(NeuralPipelineGoal.StemSeparation);
        });
        roomNeuralPrepBtn.TextSize = 11.2f;
        roomNeuralPrepBtn.SetSingleLine(true);
        roomNeuralPrepBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomNeuralPrepBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        neuralPrepRow.AddView(roomNeuralPrepBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        mixerCard.AddView(neuralPrepRow);

        mixerContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        mixerContainer.SetPadding(0, Dp(6), 0, 0);
        mixerCard.AddView(mixerContainer);
        page.AddView(mixerCard);

        RefreshFilterButtonsUi();
        RefreshNeuralPrepUi();
        RefreshSignalChainBarUi();
        return page;
    }

    void ToggleRoomHrtfVirtualizer() {
        bool next = !store.HeadphoneVirtualizer;
        store.HeadphoneVirtualizer = next;
        playerEngine.HeadphoneVirtualizer = next;
        Services.BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
        Services.AutoEqService.SyncBinauralModeToEngine(store, playerEngine);
        RefreshBinauralProfileUi();
        RefreshPersonalizationUi();
        RefreshAutoEqUi();
        RefreshMusicPlayerControlsUi();
        RefreshRoomTransportButtons();
        refreshBentoTilesUi?.Invoke();
    }

    HorizontalScrollView BuildActiveSignalChainBar() {
        HorizontalScrollView scroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout row = HRow();
        row.SetPadding(0, Dp(2), 0, Dp(2));

        Button MakeStagePill(string text, Action onTapWhenTrackLoaded) {
            Button b = PillButton(text, () => {
                CavernizeTrack active = GetActiveTrack();
                if (LoadedFile == null || active == null || !active.Supported) {
                    Toast.MakeText(this, S["SignalChainNoTrackToast"], ToastLength.Short).Show();
                    return;
                }
                onTapWhenTrackLoaded?.Invoke();
            });
            b.TextSize = 11.2f;
            b.SetSingleLine(true);
            b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            b.SetMinimumHeight(Dp(34));
            b.SetPadding(Dp(11), Dp(5), Dp(11), Dp(5));
            return b;
        }

        void AddChainArrow() {
            TextView arr = Label("→", 11f, OneUiTheme.TextMuted);
            arr.SetIncludeFontPadding(false);
            arr.SetSingleLine(true);
            arr.SetPadding(Dp(4), 0, Dp(4), 0);
            row.AddView(arr, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }

        signalChainDecoderPill = MakeStagePill(S["SignalChainDecoderIdle"], ShowTechnicalStreamDialog);
        signalChainUpmixPill = MakeStagePill(S["SignalChainUpmixDirect"], () => {
            if (tabContainers != null && tabContainers.Length > 1 && tabContainers[1] is ScrollView roomScroll && mixerCardRef != null) {
                roomScroll.SmoothScrollTo(0, Math.Max(0, mixerCardRef.Top - Dp(12)));
            }
            if (!isNeuralPrepRunning && !(playerEngine?.NeuralStage?.HasPlayableSeparatedStems ?? false)) {
                PrepareActiveTrackNeuralStemsAsync(NeuralPipelineGoal.StemSeparation);
            }
        });
        signalChainLgsrPill = MakeStagePill(S["SignalChainLgsrIdle"], () => {
            if (playerEngine != null) {
                playerEngine.LgsrBypassed = !playerEngine.LgsrBypassed;
                EnsureXRayInspectorExpanded();
                RefreshSpatialXRayHud();
                RefreshLgsrUi();
                RefreshSignalChainBarUi();
            }
        });
        signalChainRoomHrtfPill = MakeStagePill(S["SignalChainHrtfDirect"], ShowBinauralProfileSelectorDialog);
        signalChainAutoEqLoudnessPill = MakeStagePill(S["SignalChainEqLoudIdle"], () => SwitchTab(4));

        row.AddView(signalChainDecoderPill);
        AddChainArrow();
        row.AddView(signalChainUpmixPill);
        AddChainArrow();
        row.AddView(signalChainLgsrPill);
        AddChainArrow();
        row.AddView(signalChainRoomHrtfPill);
        AddChainArrow();
        row.AddView(signalChainAutoEqLoudnessPill);

        scroll.AddView(row);
        return scroll;
    }

    void RefreshSignalChainBarUi() {
        if (uiDestroyed || signalChainDecoderPill == null) {
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        bool hasTrack = LoadedFile != null && active != null && active.Supported;

        void StyleStagePill(Button b, string text, bool isStageActive, Color accent) {
            if (b == null) return;
            b.Text = text;
            b.Background = isStageActive
                ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(accent, 74), OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(accent, 180))
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, isStageActive ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            b.SetTextColor(isStageActive ? ColText : OneUiTheme.TextSecondary);
        }

        string decLabel = hasTrack
            ? string.Format(S["SignalChainDecoderFmt"], active.FormatHeader)
            : S["SignalChainDecoderIdle"];
        StyleStagePill(signalChainDecoderPill, decLabel, hasTrack, OneUiTheme.IconSliderMedia);

        bool hasStems = playerEngine?.NeuralStage?.HasPlayableSeparatedStems == true;
        int stemCnt = playerEngine?.NeuralStage?.ActiveSeparatedTrack?.Stems?.Count ?? 0;
        string upmixLabel;
        bool upmixActive = false;
        if (isNeuralPrepRunning) {
            upmixLabel = string.Format(S["NeuralPrepBusyFmt"], neuralPrepProgress);
            upmixActive = true;
        } else if (hasStems) {
            upmixLabel = string.Format(S["SignalChainUpmixAiActive"], stemCnt);
            upmixActive = true;
        } else if (store?.CavernizeUpmix == true) {
            upmixLabel = S["SignalChainUpmixCavernize"];
            upmixActive = true;
        } else if (store?.MatrixUpmixing == true) {
            upmixLabel = S["SignalChainUpmixMatrix"];
            upmixActive = true;
        } else {
            upmixLabel = S["SignalChainUpmixDirect"];
        }
        StyleStagePill(signalChainUpmixPill, upmixLabel, hasTrack && upmixActive, OneUiTheme.IconAtmosUpmix);

        bool lgsrLive = playerEngine?.LgsrDiagnostics.IsActivePreHrtf == true;
        bool lgsrByp = playerEngine?.LgsrBypassed == true;
        string lgsrLabel = lgsrByp
            ? S["SignalChainLgsrBypass"]
            : (lgsrLive ? S["SignalChainLgsrActive"] : (store?.LgsrEnabled == true ? S["SignalChainLgsrIdle"] : S["SignalChainLgsrOff"]));
        StyleStagePill(signalChainLgsrPill, lgsrLabel, hasTrack && !lgsrByp && (lgsrLive || store?.LgsrEnabled == true), OneUiTheme.IconStemsCalib);

        bool hrtfOn = store?.HeadphoneVirtualizer == true;
        string hrtfLabel = hrtfOn
            ? string.Format(S["SignalChainHrtfOnFmt"], Services.BinauralProfileService.GetActiveDistanceShortText(store, S))
            : S["SignalChainHrtfDirect"];
        StyleStagePill(signalChainRoomHrtfPill, hrtfLabel, hrtfOn, OneUiTheme.IconSpatial);

        bool autoEqOn = store?.AutoEqEnabled == true && !string.IsNullOrEmpty(store.AutoEqProfileId);
        bool loudOn = store?.Iso226Enabled == true || (store?.LoudnessNormModeIndex ?? 0) > 0 || store?.DialogEnhancementEnabled == true;
        string eqLoudLabel = (autoEqOn && loudOn)
            ? S["SignalChainEqLoudActive"]
            : (autoEqOn ? S["SignalChainEqActive"] : (loudOn ? S["SignalChainLoudActive"] : S["SignalChainEqLoudIdle"]));
        StyleStagePill(signalChainAutoEqLoudnessPill, eqLoudLabel, autoEqOn || loudOn, OneUiTheme.IconStudioExport);
    }

    LinearLayout BuildMiniTransportStrip() {
        LinearLayout stripCard = new(this) { Orientation = Orientation.Vertical };
        stripCard.Background = OneUiTheme.CreateGlassCardDrawable(this, null, 20f);
        stripCard.Elevation = Dp(5);
        stripCard.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        stripCard.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        };

        LinearLayout MakeMiniIconBtn(OneUiIconType iconType, int iconDp, bool isPlayPrimary, Color tint, Action onClick, out OneUiIconView iconOut) {
            LinearLayout btn = new(this) { Orientation = Orientation.Horizontal, BaselineAligned = false };
            btn.SetGravity(GravityFlags.Center);
            btn.Background = isPlayPrimary
                ? OneUiTheme.CreateGlassCardDrawable(this, Color.Argb(58, 255, 255, 255), 999f, OneUiTheme.WithAlpha(ColAccent, 185))
                : OneUiTheme.CreateGlassCardDrawable(this, Color.Argb(28, 255, 255, 255), 999f, Color.Argb(44, 255, 255, 255));
            OneUiTheme.AttachRecoil(btn, 0.88f);
            iconOut = new OneUiIconView(this, iconType, tint);
            btn.AddView(iconOut, new LinearLayout.LayoutParams(Dp(iconDp), Dp(iconDp)));
            btn.Click += (_, _) => onClick?.Invoke();
            return btn;
        }

        LinearLayout row = HRow();
        roomTransportPlayBtn = MakeMiniIconBtn(OneUiIconType.Play, 19, true, Color.White, TogglePlayPause, out roomTransportPlayIcon);
        LinearLayout back10Btn = MakeMiniIconBtn(OneUiIconType.Rewind10, 16, false, Color.Argb(210, 252, 252, 255), () => SeekRelative(-10), out _);
        LinearLayout fwd10Btn = MakeMiniIconBtn(OneUiIconType.Forward10, 16, false, Color.Argb(210, 252, 252, 255), () => SeekRelative(10), out _);
        roomTransportLoopBtn = MakeMiniIconBtn(OneUiIconType.RepeatLoop, 15, false, Color.Argb(215, 252, 252, 255), ToggleLoopMode, out roomTransportLoopIcon);

        void ToggleInlineSeekDrawer() {
            isMiniSeekExpanded = !isMiniSeekExpanded;
            SetExpandedWithAnimation(roomMiniSeekDrawer, isMiniSeekExpanded);
        }

        roomMiniTimeText = Label("00:00 / 00:00", 11.2f, OneUiTheme.TextSecondary);
        roomMiniTimeText.SetIncludeFontPadding(false);
        roomMiniTimeText.SetSingleLine(true);
        roomMiniTimeText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomMiniTimeText.Typeface = OneUiTheme.GetAppleFontMedium(this);
        roomMiniTimeText.SetPadding(Dp(6), Dp(4), Dp(6), Dp(4));
        roomMiniTimeText.Click += (_, _) => ToggleInlineSeekDrawer();

        roomMiniVolChip = PillButton(string.Format(S["RoomMiniVolFmt"], (int)Math.Round(store.PlayerVolume * 100)), ToggleInlineSeekDrawer);
        roomMiniVolChip.TextSize = 10.8f;
        roomMiniVolChip.SetSingleLine(true);
        roomMiniVolChip.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomMiniVolChip.SetMinimumHeight(Dp(32));
        roomMiniVolChip.SetPadding(Dp(9), Dp(4), Dp(9), Dp(4));

        roomMiniHrtfChip = PillButton(store.HeadphoneVirtualizer ? S["BentoHrtf"] : S["PlayerDirectShort"], ToggleRoomHrtfVirtualizer);
        roomMiniHrtfChip.TextSize = 10.8f;
        roomMiniHrtfChip.SetSingleLine(true);
        roomMiniHrtfChip.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomMiniHrtfChip.SetMinimumHeight(Dp(32));
        roomMiniHrtfChip.SetPadding(Dp(9), Dp(4), Dp(9), Dp(4));

        row.AddView(roomTransportPlayBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)) { RightMargin = Dp(6) });
        row.AddView(back10Btn, new LinearLayout.LayoutParams(Dp(32), Dp(32)) { RightMargin = Dp(4) });
        row.AddView(fwd10Btn, new LinearLayout.LayoutParams(Dp(32), Dp(32)) { RightMargin = Dp(4) });
        row.AddView(roomMiniTimeText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        row.AddView(roomMiniVolChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(5) });
        row.AddView(roomMiniHrtfChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(5) });
        row.AddView(roomTransportLoopBtn, new LinearLayout.LayoutParams(Dp(32), Dp(32)));
        stripCard.AddView(row);

        roomMiniSeekDrawer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isMiniSeekExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        roomMiniSeekDrawer.SetPadding(0, Dp(8), 0, Dp(2));

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
                if (roomMiniTimeText != null) {
                    roomMiniTimeText.Text = $"{curStr} / {totStr}";
                }
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
        roomMiniSeekDrawer.AddView(playbackSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        stripCard.AddView(roomMiniSeekDrawer);

        RefreshRoomTransportButtons();
        return stripCard;
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
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        TextView stateTv = Label("", 11f, OneUiTheme.TextSub);
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
            stateTv.Text = on ? S["StatusOnTag"] : S["StatusOffTag"];
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
        if (trackSourceItems == null || trackSourceItems.Count == 0 || GetActiveTrack() == null) {
            Toast.MakeText(this, S["NoTrack"], ToastLength.Short).Show();
            return;
        }
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
        if (trackSourceItems == null || trackSourceItems.Count == 0 || GetActiveTrack() == null) {
            Toast.MakeText(this, S["NoTrack"], ToastLength.Short).Show();
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
        if (trackSourceItems == null || trackSourceItems.Count == 0 || GetActiveTrack() == null) {
            fallbackWhenNoTrack?.Invoke(true);
            Toast.MakeText(this, S["NoTrack"], ToastLength.Short).Show();
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
        bool soloStemsMic = !dynCeil && IsCategorySoloActive(item =>
            item.Layer == SourceLayer.NeuralStem || item.Layer == SourceLayer.RestoredResidual || item.Layer == SourceLayer.LiveMicrophone);
        bool soloBeds = !dynCeil && IsCategorySoloActive(item => item.IsBed);
        bool noneActive = !dynCeil && !solo3D && !soloStemsMic && !soloBeds && !trackSourceItems.Any(x => x.IsSolo);

        void Style(Button b, bool active, Color accent) {
            if (b == null) return;
            b.Background = active
                ? OneUiTheme.CreateRippleRoundedDrawable(this, accent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        Style(filterAllBtn, noneActive, OneUiTheme.IconSpatial);
        Style(filter3DBtn, solo3D, OneUiTheme.IconAtmosUpmix);
        Style(filterStemsMicBtn, soloStemsMic, OneUiTheme.IconSliderMedia);
        Style(filterHeightBtn, dynCeil, OneUiTheme.IconStemsCalib);
        Style(filterBedsBtn, soloBeds, OneUiTheme.IconStudioExport);
    }

    void RefreshNeuralPrepUi() {
        if (uiDestroyed) {
            return;
        }
        bool hasStems = playerEngine?.NeuralStage?.HasPlayableSeparatedStems == true;
        bool live3D = playerEngine?.IsLiveNeuralStemPlaybackActive == true;
        int stemCount = playerEngine?.NeuralStage?.ActiveSeparatedTrack?.Stems?.Count ?? 0;

        if (musicLyricsAiPrepBtn != null) {
            if (isNeuralPrepRunning) {
                musicLyricsAiPrepBtn.Text = string.Format(S["NeuralPrepBusyFmt"], neuralPrepProgress);
                musicLyricsAiPrepBtn.Background = OneUiTheme.CreateRoundedDrawable(
                    this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 82), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 185), 1f);
            } else if (hasStems) {
                musicLyricsAiPrepBtn.Text = S["NeuralPrepReadyBadge"];
                musicLyricsAiPrepBtn.Background = OneUiTheme.CreateRoundedDrawable(
                    this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 72), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 175), 1f);
            } else {
                musicLyricsAiPrepBtn.Text = S["NeuralPrepBtn"];
                musicLyricsAiPrepBtn.Background = OneUiTheme.CreateRoundedDrawable(
                    this, Color.Argb(36, 255, 255, 255), 99f, Color.Argb(54, 255, 255, 255), 0.9f);
            }
        }
        if (musicLyricsAiStatusText != null) {
            if (isNeuralPrepRunning && !string.IsNullOrEmpty(neuralPrepStatusText)) {
                musicLyricsAiStatusText.Text = $"{string.Format(S["NeuralPrepBusyFmt"], neuralPrepProgress)} · {neuralPrepStatusText}";
                musicLyricsAiStatusText.SetTextColor(OneUiTheme.FunctionalOrange);
                musicLyricsAiStatusText.Visibility = ViewStates.Visible;
            } else if (store != null && store.LiveMicEnabled && playerEngine?.VocalMixMatchStage != null) {
                VocalMixDnaProfile dna = playerEngine.VocalMixMatchStage.ActiveDna;
                string vpTag = playerEngine.VocalMixMatchStage.HasMicVoiceprint
                    ? S["VocalSingDockVpOn"]
                    : S["VocalSingDockVpOff"];
                if (dna.IsExtractedFromSong) {
                    string drrTag = dna.HasMeasuredDereverb && float.IsFinite(dna.MeasuredDrrDb)
                        ? string.Format(S["VocalDnaDrrTagFmt"], dna.MeasuredDrrDb)
                        : string.Empty;
                    musicLyricsAiStatusText.Text = string.Format(
                        S["VocalSingDockDnaReadyFmt"],
                        dna.ReverbRt60Sec,
                        drrTag,
                        vpTag);
                    musicLyricsAiStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
                } else {
                    musicLyricsAiStatusText.Text = string.Format(S["VocalSingDockNeutralFmt"], vpTag);
                    musicLyricsAiStatusText.SetTextColor(OneUiTheme.TextSub);
                }
                musicLyricsAiStatusText.Visibility = ViewStates.Visible;
            } else {
                musicLyricsAiStatusText.Visibility = ViewStates.Gone;
            }
        }

        if (roomNeuralPrepBtn != null) {
            if (isNeuralPrepRunning) {
                roomNeuralPrepBtn.Text = S["NeuralPrepCancelBtn"];
                roomNeuralPrepBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.FunctionalOrange, OneUiTheme.PillCornerDp);
            } else if (hasStems) {
                roomNeuralPrepBtn.Text = S["NeuralPrepReadyBadge"];
                roomNeuralPrepBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.FunctionalGreen, OneUiTheme.PillCornerDp);
            } else {
                roomNeuralPrepBtn.Text = S["NeuralPrep3DBtn"];
                roomNeuralPrepBtn.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            }
        }
        if (roomNeuralStatusText != null) {
            if (isNeuralPrepRunning) {
                roomNeuralStatusText.Text = $"{string.Format(S["NeuralPrepBusyFmt"], neuralPrepProgress)} · {neuralPrepStatusText}";
                roomNeuralStatusText.SetTextColor(OneUiTheme.FunctionalOrange);
            } else if (live3D) {
                string modeLabel = store.NeuralSpatialUpmixModeIndex == 2 ? S["NeuralUpmixHybrid714"] : S["NeuralUpmixStems3D"];
                roomNeuralStatusText.Text = string.Format(S["NeuralLiveStemsActiveFmt"], stemCount, modeLabel);
                roomNeuralStatusText.SetTextColor(OneUiTheme.FunctionalGreen);
            } else if (hasStems) {
                roomNeuralStatusText.Text = $"{S["NeuralPrepReadyBadge"]} ({stemCount})";
                roomNeuralStatusText.SetTextColor(OneUiTheme.IconSliderMedia);
            } else if (GetActiveTrack() == null) {
                roomNeuralStatusText.Text = S["NeuralPrepNoTrackToast"];
                roomNeuralStatusText.SetTextColor(OneUiTheme.TextSub);
            } else {
                Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(
                    NeuralPipelineGoal.StemSeparation, store.BuildNeuralAudioConfig());
                string summary = plan != null && plan.IsRunnable && !string.IsNullOrEmpty(plan.Summary)
                    ? plan.Summary.Replace("libonnxruntime.so", "ONNX Runtime")
                    : S["RoomNeuralIdleHint"];
                roomNeuralStatusText.Text = summary;
                roomNeuralStatusText.SetTextColor(OneUiTheme.TextSub);
            }
        }
        RefreshSignalChainBarUi();
    }

    void EnsureXRayInspectorExpanded() {
        if (!store.RoomXRayInspectorExpanded) {
            store.RoomXRayInspectorExpanded = true;
            if (xrayInspectorBody != null) {
                SetExpandedWithAnimation(xrayInspectorBody, true);
            }
            if (xrayInspectorToggleBtn != null) {
                xrayInspectorToggleBtn.Text = S["XRayInspectorHideBtn"];
            }
        }
    }

    LinearLayout BuildSpatialXRayInspectorCard() {
        LinearLayout card = Card();

        LinearLayout hdr = HRow();
        hdr.SetPadding(0, 0, 0, Dp(4));
        hdr.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Sliders, OneUiTheme.IconStemsCalib, 30));
        TextView titleTv = SectionTitle(S["XRayInspectorTitle"]);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titleTv.SetPadding(Dp(8), 0, Dp(8), 0);
        hdr.AddView(titleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        bool expanded = store.RoomXRayInspectorExpanded || store.RoomXRayOverlayEnabled;
        if (expanded && !store.RoomXRayInspectorExpanded) {
            store.RoomXRayInspectorExpanded = true;
        }
        xrayInspectorToggleBtn = PillButton(expanded ? S["XRayInspectorHideBtn"] : S["XRayInspectorShowBtn"], () => {
            bool next = !store.RoomXRayInspectorExpanded;
            store.RoomXRayInspectorExpanded = next;
            SetExpandedWithAnimation(xrayInspectorBody, next);
            xrayInspectorToggleBtn.Text = next ? S["XRayInspectorHideBtn"] : S["XRayInspectorShowBtn"];
            if (next) {
                RefreshSpatialXRayHud();
            }
        });
        xrayInspectorToggleBtn.TextSize = 11.2f;
        xrayInspectorToggleBtn.SetSingleLine(true);
        xrayInspectorToggleBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        xrayInspectorToggleBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        xrayInspectorToggleBtn.SetMinimumHeight(Dp(34));
        hdr.AddView(xrayInspectorToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(hdr);

        xrayInspectorBody = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = expanded ? ViewStates.Visible : ViewStates.Gone
        };
        xrayInspectorBody.SetPadding(0, Dp(6), 0, 0);

        // Controls: 3D Heatmap Overlay + 6DoF Acoustic Propagation Delay (343 m/s)
        xrayOverlaySwitch = AddSwitchRow(xrayInspectorBody, S["XRayOverlayToggle"], store.RoomXRayOverlayEnabled, on => {
            store.RoomXRayOverlayEnabled = on;
            if (roomView != null) {
                roomView.IsXRayEnabled = on;
                roomView.Invalidate();
            }
            if (on) {
                EnsureXRayInspectorExpanded();
                RefreshSpatialXRayHud();
            }
            UpdateRoomCanvas();
        });

        // 3-Band Spectral Selector Row (All / Low / Mid / High)
        HorizontalScrollView bandScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout bandRow = HRow();
        bandRow.SetPadding(0, Dp(4), 0, Dp(8));
        string[] bandLabels = [S["XRayBandAll"], S["XRayBandLow"], S["XRayBandMid"], S["XRayBandHigh"]];
        xrayBandBtns = new Button[4];
        if (playerEngine?.SceneHub != null) {
            playerEngine.SceneHub.ActiveXRayBandMode = (SpatialXRayBandMode)Math.Clamp(store.RoomXRayBandMode, 0, 3);
        }
        for (int b = 0; b < 4; b++) {
            int idx = b;
            Button btn = FilterBtn(bandLabels[b], () => {
                store.RoomXRayBandMode = idx;
                SpatialXRayBandMode mode = (SpatialXRayBandMode)idx;
                playerEngine.SceneHub.ActiveXRayBandMode = mode;
                if (roomView != null) {
                    roomView.XRayBandMode = mode;
                    roomView.Invalidate();
                }
                RefreshXRayBandButtonsUi();
            });
            xrayBandBtns[b] = btn;
            bandRow.AddView(btn);
        }
        bandScroll.AddView(bandRow);
        xrayInspectorBody.AddView(bandScroll);
        RefreshXRayBandButtonsUi();

        AddSwitchRow(xrayInspectorBody, S["XRayPropDelay6DoF"], store.PropagationDelay6DoFEnabled, on => {
            store.PropagationDelay6DoFEnabled = on;
            playerEngine.PropagationDelay6DoFEnabled = on;
            UpdateRoomCanvas();
        });

        // Instant A/B Bypass & Reset Object Pins (Compact 2+1 layout to prevent <=360dp clipping)
        LinearLayout actionRowTop = HRow();
        actionRowTop.SetPadding(0, Dp(4), 0, Dp(6));
        xrayLgsrBypassBtn = PillButton(S["XRayLgsrBypassBtn"], () => {
            playerEngine.LgsrBypassed = !playerEngine.LgsrBypassed;
            RefreshSpatialXRayHud();
            RefreshLgsrUi();
        });
        xrayVocalBypassBtn = PillButton(S["XRayVocalBypassBtn"], () => {
            playerEngine.VocalChainBypassed = !playerEngine.VocalChainBypassed;
            RefreshSpatialXRayHud();
            RefreshLyricsSingDockUi();
        });
        Button resetPinsBtn = PillButton(S["XRayResetPinsBtn"], () => {
            playerEngine.ResetAllCustomPositions();
            UpdateRoomCanvas();
            Toast.MakeText(this, S["XRayPinsResetToast"], ToastLength.Short).Show();
        });
        xrayLgsrBypassBtn.TextSize = 11.2f;
        xrayLgsrBypassBtn.SetSingleLine(true);
        xrayLgsrBypassBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        xrayVocalBypassBtn.TextSize = 11.2f;
        xrayVocalBypassBtn.SetSingleLine(true);
        xrayVocalBypassBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        resetPinsBtn.TextSize = 11.2f;
        resetPinsBtn.SetSingleLine(true);
        resetPinsBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        actionRowTop.AddView(xrayLgsrBypassBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        actionRowTop.AddView(xrayVocalBypassBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        xrayInspectorBody.AddView(actionRowTop);
        xrayInspectorBody.AddView(resetPinsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // Essential Summary & Pro Acoustic Telemetry Disclosure Bar
        LinearLayout essentialTile = new(this) { Orientation = Orientation.Vertical };
        essentialTile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
        essentialTile.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        LinearLayout essentialHeaderRow = HRow();
        xrayEssentialSummaryTv = Label("—", 11.3f, OneUiTheme.TextSecondary);
        xrayEssentialSummaryTv.SetPadding(0, 0, Dp(8), 0);
        xrayProTelemetryToggleBtn = PillButton(isProTelemetryExpanded ? S["XRayModeProBtn"] : S["XRayModeEssentialBtn"], () => {
            isProTelemetryExpanded = !isProTelemetryExpanded;
            SetExpandedWithAnimation(xrayProTelemetryContainer, isProTelemetryExpanded);
            xrayProTelemetryToggleBtn.Text = isProTelemetryExpanded ? S["XRayModeProBtn"] : S["XRayModeEssentialBtn"];
            if (isProTelemetryExpanded) {
                RefreshSpatialXRayHud();
            }
        });
        xrayProTelemetryToggleBtn.TextSize = 10.8f;
        xrayProTelemetryToggleBtn.SetSingleLine(true);
        xrayProTelemetryToggleBtn.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        xrayProTelemetryToggleBtn.SetMinimumHeight(Dp(32));
        xrayProTelemetryToggleBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        essentialHeaderRow.AddView(xrayEssentialSummaryTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        essentialHeaderRow.AddView(xrayProTelemetryToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        essentialTile.AddView(essentialHeaderRow);
        xrayInspectorBody.AddView(essentialTile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(7)
        });

        xrayProTelemetryContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isProTelemetryExpanded ? ViewStates.Visible : ViewStates.Gone
        };

        // Live Pro Acoustic Telemetry Sub-Tiles (gated behind Essential / Pro disclosure toggle)
        LinearLayout AddXRaySubTile(string headerText, out TextView metricsOut) {
            LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
            tile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
            tile.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
            TextView hTv = Label(headerText, 11.2f, OneUiTheme.PrimaryHigh);
            hTv.SetTypeface(null, TypefaceStyle.Bold);
            hTv.SetSingleLine(true);
            hTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            metricsOut = Label("—", 11.5f, OneUiTheme.TextSecondary);
            metricsOut.SetPadding(0, Dp(3), 0, 0);
            tile.AddView(hTv);
            tile.AddView(metricsOut);
            xrayProTelemetryContainer.AddView(tile, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(7)
            });
            return tile;
        }

        AddXRaySubTile(S["XRaySectionSoundfield"], out xraySoundfieldMetricsTv);
        AddXRaySubTile(S["XRaySectionLgsr"], out xrayLgsrMetricsTv);
        AddXRaySubTile(S["XRaySectionVocal"], out xrayVocalMetricsTv);
        xrayInspectorBody.AddView(xrayProTelemetryContainer);

        card.AddView(xrayInspectorBody);
        RefreshSpatialXRayHud();
        return card;
    }

    void RefreshXRayBandButtonsUi() {
        if (xrayBandBtns == null) return;
        int active = Math.Clamp(store.RoomXRayBandMode, 0, 3);
        Color[] accents = [OneUiTheme.IconSpatial, OneUiTheme.IconAtmosUpmix, OneUiTheme.IconSliderMedia, OneUiTheme.IconStemsCalib];
        for (int i = 0; i < xrayBandBtns.Length; i++) {
            Button b = xrayBandBtns[i];
            if (b == null) continue;
            bool on = i == active;
            b.Background = on
                ? OneUiTheme.CreateRippleRoundedDrawable(this, accents[i], OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            b.SetTypeface(null, on ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
    }

    void RefreshSpatialXRayHud() {
        if (playerEngine == null || xraySoundfieldMetricsTv == null) {
            return;
        }
        SoundfieldTelemetry tel = playerEngine.SceneHub.Telemetry;
        LoudnessStandardsDiagnostics loud = playerEngine.LoudnessDiagnostics;
        LgsrDiagnostics lgsr = playerEngine.LgsrDiagnostics;
        VocalChainTelemetry vocal = playerEngine.SceneHub.VocalChainDiagnostics;
        SpatialProgramMetadata meta = playerEngine.SceneHub.ProgramMetadata;

        float cohPct = Math.Clamp(tel.Coherence * 100f, 0f, 100f);
        float diffPct = Math.Clamp(tel.Diffuseness * 100f, 0f, 100f);
        string mLufs = loud.MomentaryLufs > -110f ? $"{loud.MomentaryLufs:0.0}" : "--";
        string iLufs = loud.IntegratedLufs > -110f ? $"{loud.IntegratedLufs:0.0}" : "--";
        string tpStr = loud.TruePeakDbtp > -110f ? $"{loud.TruePeakDbtp:0.0}" : "--";
        if (xrayEssentialSummaryTv != null) {
            xrayEssentialSummaryTv.Text = string.Format(S["XRayEssentialSummaryFmt"], cohPct, diffPct, iLufs, tpStr);
        }
        xraySoundfieldMetricsTv.Text = string.Format(
            S["XRaySoundfieldTelemetryFmt"],
            cohPct,
            diffPct,
            mLufs,
            iLufs,
            tpStr,
            loud.LoudnessRangeLu,
            MathF.Abs(loud.LimiterGainReductionDb));

        float cplHz = lgsr.CouplingStartHz > 0f ? lgsr.CouplingStartHz : meta.CouplingStartHz;
        float spxHz = lgsr.SpxStartHz > 0f ? lgsr.SpxStartHz : meta.SpxStartHz;
        string lgsrState = playerEngine.LgsrBypassed ? S["XRayBypassActive"] : (lgsr.IsActivePreHrtf ? S["XRayActiveLive"] : S["XRayStandbyState"]);
        xrayLgsrMetricsTv.Text = string.Format(
            S["XRayLgsrTelemetryFmt"],
            lgsrState,
            cplHz,
            spxHz,
            lgsr.SyncOffsetSamples,
            lgsr.DriftPpm,
            lgsr.ActiveFusionRatio * 100f);

        string vocState = playerEngine.VocalChainBypassed ? S["XRayBypassActive"] : (tel.IsLiveMicActive ? S["XRayActiveLive"] : S["XRayStandbyState"]);
        float compGr = MathF.Abs(vocal.CompressorReductionDb);
        xrayVocalMetricsTv.Text = string.Format(
            S["XRayVocalTelemetryFmt"],
            vocState,
            vocal.InputDbfs,
            vocal.OutputDbfs,
            MathF.Abs(vocal.GateReductionDb),
            MathF.Abs(vocal.DeEsserReductionDb),
            compGr,
            MathF.Abs(vocal.SidechainPocketDuckDb),
            tel.FullDuplexRoundTripMs);

        if (xrayLgsrBypassBtn != null) {
            bool byp = playerEngine.LgsrBypassed;
            xrayLgsrBypassBtn.Text = $"{S["XRayLgsrBypassBtn"]}: {(byp ? S["XRayBypassActive"] : S["XRayActiveLive"])}";
            xrayLgsrBypassBtn.Background = byp
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColDanger, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        }
        if (xrayVocalBypassBtn != null) {
            bool byp = playerEngine.VocalChainBypassed;
            xrayVocalBypassBtn.Text = $"{S["XRayVocalBypassBtn"]}: {(byp ? S["XRayBypassActive"] : S["XRayActiveLive"])}";
            xrayVocalBypassBtn.Background = byp
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColDanger, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        }
        RefreshSignalChainBarUi();
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

        float ctrlAlpha = locked ? 0.45f : 1f;
        for (int i = 0; i < roomPresetBtns.Count; i++) {
            Button pb = roomPresetBtns[i];
            if (pb != null) {
                pb.Enabled = !locked;
                pb.Alpha = ctrlAlpha;
            }
        }
        if (roomResetAllBtn != null) {
            roomResetAllBtn.Enabled = !locked;
            roomResetAllBtn.Alpha = ctrlAlpha;
        }
        OneUiSeekBar[] posBars = [seekBarPosX, seekBarPosZ, seekBarPosY, seekBarYaw, seekBarPitch];
        for (int i = 0; i < posBars.Length; i++) {
            OneUiSeekBar sb = posBars[i];
            if (sb != null) {
                sb.Enabled = !locked;
                sb.Alpha = ctrlAlpha;
            }
        }
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
        if (roomMiniVolChip != null && store != null) {
            roomMiniVolChip.Text = string.Format(S["RoomMiniVolFmt"], (int)Math.Round(store.PlayerVolume * 100));
        }
        if (roomMiniHrtfChip != null && store != null) {
            bool hrtf = store.HeadphoneVirtualizer;
            roomMiniHrtfChip.Text = hrtf ? S["BentoHrtf"] : S["PlayerDirectShort"];
            roomMiniHrtfChip.Background = hrtf
                ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 78), OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 185))
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            roomMiniHrtfChip.SetTypeface(null, hrtf ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        RefreshSignalChainBarUi();
    }

    void SyncPlaybackUi(bool isPlaying, double curSeconds, double totSeconds) {
        RefreshRoomTransportButtons();
        int prog = totSeconds > 0 ? Math.Clamp((int)(curSeconds / totSeconds * 1000.0), 0, 1000) : 0;
        string curStr = $"{TimeSpan.FromSeconds(Math.Max(0, curSeconds)):mm\\:ss}";
        string totStr = $"{TimeSpan.FromSeconds(Math.Max(0, totSeconds)):mm\\:ss}";
        if (roomMiniTimeText != null && !isUserSeeking) {
            roomMiniTimeText.Text = $"{curStr} / {totStr}";
        }

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
                ? string.Format(S["RoomTelemetryHeadTrackerFmt"], playerEngine.SceneHub.ActiveHeadTrackerName)
                : string.Empty;
            roomTelemetryText.Text = string.Format(S["RoomTelemetryCoordsFmt"], p.X, p.Y, p.Z, r.Y, r.X, htSuffix);
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
        roomView.UpdateXRayState(playerEngine.SceneHub.XRayPowermap, playerEngine.SceneHub.Telemetry);
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
            Vector3 p = item.NormalizedPosition;
            string delayTag = !item.IsLFE && item.PropagationDelayMs > 0.05f ? $" • Δt {item.PropagationDelayMs:0.0}ms" : string.Empty;
            string pinTag = item.HasCustomPosition ? $" • [{S["XRayObjectPinnedBadge"]}]" : string.Empty;
            if (item.IsDynamicObject || dynCeil || item.HasCustomPosition || delayTag.Length > 0) {
                r.SubTv.Text = dynCeil && !item.IsDynamicallyFilteredOut
                    ? $"({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}){delayTag}{pinTag} • {S["CeilingLiveBadge"]}"
                    : $"({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}){delayTag}{pinTag}";
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
            nameTv.SetSingleLine(true);
            nameTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            TextView subTv = Label(
                string.Format(S["MixerSourcePosFmt"], item.NormalizedPosition.X, item.NormalizedPosition.Y, item.NormalizedPosition.Z),
                10.5f,
                ColMuted);
            subTv.SetSingleLine(true);
            subTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
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
                    RefreshLgsrUi();
                    if (activeTab == 1 && store.RoomXRayInspectorExpanded) {
                        RefreshSpatialXRayHud();
                    }
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
