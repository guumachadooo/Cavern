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
    LinearLayout xrayInspectorCardRef;

    /// <summary>
    /// Toggles the advanced Spatial X-Ray telemetry inspector inside 3D Stage (Pro/Lab only).
    /// </summary>
    void Refresh3DStageProVisibility() {
        if (xrayInspectorCardRef != null) {
            xrayInspectorCardRef.Visibility = isSettingsProMode ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    RoomSimulatorView roomView;
    LinearLayout roomLockBtn;
    OneUiIconView roomLockIcon;
    TextView roomLockText;
    Button fineTuneToggleBtn;
    LinearLayout fineTuneContainer;
    bool isFineTuneExpanded;
    OneUiSeekBar seekBarPosX, seekBarPosZ, seekBarPosY, seekBarYaw, seekBarPitch;
    OneUiWaveMediaSeekBar playbackSeekBar;
    OneUiSeekBar seekBarLfe;
    TextView roomTelemetryText;
    LinearLayout roomTransportPlayBtn;
    OneUiIconView roomTransportPlayIcon;
    LinearLayout roomTransportLoopBtn;
    OneUiIconView roomTransportLoopIcon;
    readonly List<Button> roomPresetBtns = [];
    Button roomResetAllBtn;
    Button signalChainDecoderPill, signalChainUpmixPill, signalChainLgsrPill, signalChainRoomHrtfPill, signalChainAutoEqLoudnessPill;
    LinearLayout roomMiniSeekDrawer;
    bool isMiniSeekExpanded = true;
    LinearLayout roomMiniVolDrawer;
    OneUiSeekBar roomMiniVolSeekBar;
    bool isMiniVolExpanded;
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
    OneUiInlineSelector spatialChoreoSelector;
    TextView spatialChoreoTierBadgeText;
    int choreoBuildGeneration;
    NeuralSeparatedTrack lastChoreoBuiltStemTrack;
    SpatialChoreoMode lastChoreoBuiltMode = SpatialChoreoMode.Off;
    string lastChoreoBuiltLyricsSig = string.Empty;
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
        page.SetPadding(Dp(14), Dp(4), Dp(14), Dp(116));

        // 0. Live Tappable Active Signal Chain Bar (WorkspaceSourceBar is hoisted stationary above contentFrame)
        HorizontalScrollView signalChainBar = BuildActiveSignalChainBar();
        page.AddView(signalChainBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
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
            InvertOrbitDrag = store.RoomInvertOrbitDrag,
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
        roomView.SourceSoloToggled += toggled => {
            if (playerEngine != null && playerEngine.DynamicCeilingOnly) {
                playerEngine.DynamicCeilingOnly = false;
                ceilingActiveSinceMs.Clear();
                ceilingPinUntilMs.Clear();
                IReadOnlyList<TrackSourceItem> items = trackSourceItems;
                for (int k = 0; k < items.Count; k++) {
                    items[k].IsDynamicallyFilteredOut = false;
                }
            }
            TrackSourceItem live = ResolveLiveSourceItem(toggled);
            if (live != null && toggled != null && !ReferenceEquals(live, toggled)) {
                live.IsSolo = toggled.IsSolo;
            }
            SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
            RefreshFilterButtonsUi();
            refreshBentoTilesUi?.Invoke();
            if (live != null && !ReferenceEquals(live, toggled)) {
                UpdateRoomCanvas();
            }
        };
        roomView.SourcePositionDragged += (item, normPos) => {
            TrackSourceItem live = ResolveLiveSourceItem(item) ?? item;
            playerEngine?.SetSourceCustomPosition(live, normPos);
            if (!ReferenceEquals(live, item)) {
                UpdateRoomCanvas();
            } else {
                UpdateMixerRowsLive();
            }
        };
        roomView.SourcePositionResetRequested += item => {
            TrackSourceItem live = ResolveLiveSourceItem(item) ?? item;
            playerEngine?.ClearSourceCustomPosition(live);
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
            if (!useLiveEngineSources) {
                IReadOnlyList<TrackSourceItem> items = trackSourceItems;
                for (int i = 0; i < items.Count; i++) {
                    items[i].ResetPosition();
                }
            }
            headTrackingManager?.Recenter();
            SyncRoomSlidersFromEngine();
            UpdateRoomCanvas();
        });
        roomResetAllBtn = resetRoomBtn;
        fineTuneToggleBtn.SetSingleLine(false);
        fineTuneToggleBtn.SetMaxLines(2);
        fineTuneToggleBtn.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        resetRoomBtn.SetSingleLine(false);
        resetRoomBtn.SetMaxLines(2);
        resetRoomBtn.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        roomCtrlRow.AddView(fineTuneToggleBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.25f) { RightMargin = Dp(8) });
        roomCtrlRow.AddView(resetRoomBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        roomCard.AddView(roomCtrlRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        SpatialChoreoMode initialChoreoMode = (SpatialChoreoMode)Math.Clamp(store.SpatialChoreoModeIndex, 0, 4);
        if (playerEngine?.SceneHub != null) {
            playerEngine.SceneHub.ActiveChoreoMode = initialChoreoMode;
        }
        Cavern.Neural.NeuralStemSpatializer.ActiveChoreoMode = initialChoreoMode;

        spatialChoreoSelector = new OneUiInlineSelector(this, S["SpatialChoreoLabel"]);
        spatialChoreoSelector.SetItems(new[] {
            S["SpatialChoreoOff"],
            S["SpatialChoreoSubtle"],
            S["SpatialChoreoMusical"],
            S["SpatialChoreoCinematic"],
            S["SpatialChoreoOrbit"]
        }, (int)initialChoreoMode);
        spatialChoreoSelector.SelectionChanged += idx => {
            OnSpatialChoreoModeSelected((SpatialChoreoMode)Math.Clamp(idx, 0, 4));
        };
        spatialChoreoContainer = WrapControlWithHelp(
            spatialChoreoSelector,
            S["SpatialChoreoLabel"].TrimEnd(':'),
            S["TipSpatialChoreo"],
            bottomMarginDp: 2);
        spatialChoreoContainer.SetPadding(0, Dp(8), 0, 0);
        roomCard.AddView(spatialChoreoContainer);

        spatialChoreoTierBadgeText = Label("", 11.2f, OneUiTheme.TextSub);
        spatialChoreoTierBadgeText.SetPadding(Dp(4), Dp(2), Dp(4), Dp(2));
        roomCard.AddView(spatialChoreoTierBadgeText);
        RefreshSpatialChoreoClarifierUi();

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
        LinearLayout miniTransportStrip = BuildMiniTransportStrip();
        page.AddView(miniTransportStrip);

        // 1C. W13/W55.4 Studio Spatial X-Ray Inspector Card (Pro/Lab-gated so 3D Stage stays focused on 3 essential zones)
        xrayInspectorCardRef = BuildSpatialXRayInspectorCard();
        xrayInspectorCardRef.Visibility = isSettingsProMode ? ViewStates.Visible : ViewStates.Gone;
        page.AddView(xrayInspectorCardRef);

        // 3. One UI 9 Quick Spatial Panel Card (1x2 Bento Toggles + Fat Dynamic Capsules + In-App Rolloff Selector)
        LinearLayout quickCard = Card(S["QuickPanelTitle"], OneUiIconType.Sliders, OneUiTheme.IconAtmosUpmix);

        bentoRow1 = HRow();
        bentoRow1.SetPadding(0, 0, 0, Dp(10));

        var tileHrtf = CreateBentoToggle(
            OneUiIconType.Headphones, S["BentoHrtf"],
            () => store.HeadphoneVirtualizer,
            ToggleActiveOutputSpatialization,
            OneUiTheme.IconSpatial,
            helpBody: S["TipHrtf"]);

        var tileDist = CreateBentoActionTile(
            OneUiIconType.Distance, S["BentoDist"],
            () => Services.BinauralProfileService.GetActiveDistanceShortText(store, S),
            () => store.HeadphoneVirtualizer,
            CycleThKolnDistance,
            OneUiTheme.IconSliderMedia,
            helpBody: S["TipDistSim"]);

        var tileSpeakerXtc = CreateBentoToggle(
            OneUiIconType.Speaker, S["BentoSpeakerXtc"],
            () => store.TransauralXtcEnabled,
            ToggleActiveOutputSpatialization,
            OneUiTheme.IconSpatial,
            helpBody: S["TipBentoSpeakerXtc"]);

        bentoRow1.AddView(tileHrtf.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        bentoRow1.AddView(tileDist.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bentoRow1.AddView(tileSpeakerXtc.view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        quickCard.AddView(bentoRow1);

        LinearLayout roomProfRow = HRow();
        roomProfRowRef = roomProfRow;
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
        quickCard.AddView(BuildRoomSpeakerXtcQuickRow(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) });

        refreshBentoTilesUi = () => SyncRoomBentoOutputUi(tileHrtf, tileDist, tileSpeakerXtc);
        refreshBentoTilesUi();

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
        roomNeuralStatusText.SetSingleLine(true); roomNeuralStatusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomNeuralStatusText.Click += (_, _) => ShowUnifiedAiModelPickerDialog();
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
            if (!isNeuralPrepRunning && playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems == true) {
                int stemCnt = playerEngine.NeuralStage.ActiveSpatialUpmixTrack?.Stems?.Count ?? 0;
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
        neuralPrepRow.AddView(roomNeuralPrepBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            RightMargin = Dp(6)
        });

        Button roomAiSettingsBtn = PillButton(S["OpenAiSettingsShortBtn"], () => JumpToSettingsCategory(2));
        roomAiSettingsBtn.TextSize = 11f;
        roomAiSettingsBtn.SetSingleLine(true);
        roomAiSettingsBtn.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        neuralPrepRow.AddView(roomAiSettingsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        mixerCard.AddView(neuralPrepRow);

        mixerContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        mixerContainer.SetPadding(0, Dp(6), 0, 0);
        mixerCard.AddView(mixerContainer);
        page.AddView(mixerCard);

        RegisterRoomModularBlocks(page, signalChainBar, roomCard, miniTransportStrip, xrayInspectorCardRef, quickCard, mixerCard);
        RefreshFilterButtonsUi();
        RefreshNeuralPrepUi();
        RefreshSignalChainBarUi();
        return page;
    }

    void ToggleRoomHrtfVirtualizer() => ToggleActiveOutputSpatialization();

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
            if (stage3dScrollView is ScrollView roomScroll && mixerCardRef != null) {
                roomScroll.SmoothScrollTo(0, Math.Max(0, mixerCardRef.Top - Dp(12)));
            }
            if (!isNeuralPrepRunning && !(playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems ?? false)) {
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
        signalChainRoomHrtfPill = MakeStagePill(S["SignalChainHrtfDirect"], OpenActiveOutputSpatializationSheet);
        signalChainAutoEqLoudnessPill = MakeStagePill(S["SignalChainEqLoudIdle"], () => { CloseNowPlayingSheet(); SwitchToSettingsCategory(3); });

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

        string spatialLabel = GetRoomSignalChainSpatialLabel(out bool spatialActive);
        StyleStagePill(signalChainRoomHrtfPill, spatialLabel, spatialActive, OneUiTheme.IconSpatial);

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
        roomTransportPlayBtn = MakeMiniIconBtn(OneUiIconType.Play, 17, true, Color.White, TogglePlayPause, out roomTransportPlayIcon);
        LinearLayout back10Btn = MakeMiniIconBtn(OneUiIconType.Rewind10, 15, false, Color.Argb(210, 252, 252, 255), () => SeekRelative(-10), out _);
        LinearLayout fwd10Btn = MakeMiniIconBtn(OneUiIconType.Forward10, 15, false, Color.Argb(210, 252, 252, 255), () => SeekRelative(10), out _);
        roomTransportLoopBtn = MakeMiniIconBtn(OneUiIconType.RepeatLoop, 14, false, Color.Argb(215, 252, 252, 255), ToggleLoopMode, out roomTransportLoopIcon);

        void ToggleInlineSeekDrawer() {
            isMiniSeekExpanded = !isMiniSeekExpanded;
            SetExpandedWithAnimation(roomMiniSeekDrawer, isMiniSeekExpanded);
        }

        void ToggleInlineVolDrawer() {
            isMiniVolExpanded = !isMiniVolExpanded;
            SetExpandedWithAnimation(roomMiniVolDrawer, isMiniVolExpanded);
            RefreshRoomTransportButtons();
        }

        roomMiniTimeText = Label("00:00 / 00:00", 10.6f, OneUiTheme.TextSecondary);
        roomMiniTimeText.SetIncludeFontPadding(false);
        roomMiniTimeText.SetSingleLine(false);
        roomMiniTimeText.SetMaxLines(2);
        roomMiniTimeText.Gravity = GravityFlags.CenterVertical;
        roomMiniTimeText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomMiniTimeText.Typeface = OneUiTheme.GetAppleFontMedium(this);
        roomMiniTimeText.SetMinWidth(0);
        roomMiniTimeText.SetPadding(Dp(3), Dp(4), Dp(3), Dp(4));
        roomMiniTimeText.Click += (_, _) => ToggleInlineSeekDrawer();

        roomMiniVolChip = PillButton(string.Format(S["RoomMiniVolFmt"], GetCurrentMasterVolumePercent()), ToggleInlineVolDrawer);
        roomMiniVolChip.TextSize = 10.2f;
        roomMiniVolChip.SetSingleLine(true);
        roomMiniVolChip.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        roomMiniVolChip.SetMinWidth(0);
        roomMiniVolChip.SetMinimumWidth(0);
        roomMiniVolChip.SetMinimumHeight(Dp(30));
        roomMiniVolChip.SetPadding(Dp(7), Dp(4), Dp(7), Dp(4));

        roomMiniHrtfChip = BuildRoomMiniSpatialChip();

        row.AddView(roomTransportPlayBtn, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(4) });
        row.AddView(back10Btn, new LinearLayout.LayoutParams(Dp(30), Dp(30)) { RightMargin = Dp(4) });
        row.AddView(fwd10Btn, new LinearLayout.LayoutParams(Dp(30), Dp(30)) { RightMargin = Dp(4) });
        row.AddView(roomMiniTimeText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        row.AddView(roomMiniVolChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(4) });
        row.AddView(roomMiniHrtfChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(4) });
        row.AddView(roomTransportLoopBtn, new LinearLayout.LayoutParams(Dp(30), Dp(30)));
        stripCard.AddView(row);

        roomMiniVolDrawer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isMiniVolExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        roomMiniVolDrawer.SetPadding(Dp(2), Dp(6), Dp(2), Dp(2));
        roomMiniVolSeekBar = new OneUiSeekBar(this) {
            Max = 100,
            Progress = GetCurrentMasterVolumePercent(),
            SlimVolumeMode = true,
            HasDefaultReset = false
        };
        roomMiniVolSeekBar.UserProgressChanged += v => SetMasterVolumePercent(v);
        roomMiniVolDrawer.AddView(roomMiniVolSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        stripCard.AddView(roomMiniVolDrawer);

        roomMiniSeekDrawer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = isMiniSeekExpanded ? ViewStates.Visible : ViewStates.Gone
        };
        roomMiniSeekDrawer.SetPadding(0, Dp(6), 0, Dp(2));

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
                    if (musicWaveSeekBar.IsShown) {
                        musicWaveSeekBar.Invalidate();
                    }
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
        titleTv.SetSingleLine(false);
        titleTv.SetMaxLines(2);
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
            b.SetTextColor(active ? OneUiTheme.GetContrastingTextColor(accent) : ColText);
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
        bool hasSpatialStems = playerEngine?.NeuralStage?.HasPlayableSpatialUpmixStems == true;
        bool hasKaraokeRef = playerEngine?.NeuralStage?.HasIsolatedNeuralVocalReference == true;
        bool live3D = playerEngine?.IsLiveNeuralStemPlaybackActive == true;
        int stemCount = playerEngine?.NeuralStage?.ActiveSpatialUpmixTrack?.Stems?.Count ?? 0;

        if (musicLyricsAiPrepBtn != null) {
            if (isNeuralPrepRunning && activeNeuralPrepGoal == NeuralPipelineGoal.SpatialKaraoke) {
                musicLyricsAiPrepBtn.Text = string.Format(S["NeuralPrepBusyFmt"], neuralPrepProgress);
                musicLyricsAiPrepBtn.Background = OneUiTheme.CreateRoundedDrawable(
                    this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 82), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 185), 1f);
            } else if (hasKaraokeRef) {
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
            if (isNeuralPrepRunning && activeNeuralPrepGoal == NeuralPipelineGoal.StemSeparation) {
                roomNeuralPrepBtn.Text = S["NeuralPrepCancelBtn"];
                roomNeuralPrepBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, OneUiTheme.FunctionalOrange, OneUiTheme.PillCornerDp);
            } else if (hasSpatialStems) {
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
            } else if (hasSpatialStems) {
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
        if (!IsCoordinatorUpdating) {
            RefreshSignalChainBarUi(); RefreshBottomActionBarVisuals(); RefreshSpatialChoreoClarifierUi();
        }
        SyncSpatialChoreoTrackIfNeeded();
    }

    void RefreshSpatialChoreoClarifierUi() {
        RefreshSpatialChoreoClarifierV42();
    }

    void OnSpatialChoreoModeSelected(SpatialChoreoMode mode) {
        if (uiDestroyed || store == null) {
            return;
        }
        store.SpatialChoreoModeIndex = (int)mode;
        if (playerEngine?.SceneHub != null) {
            playerEngine.SceneHub.ActiveChoreoMode = mode;
        }
        Cavern.Neural.NeuralStemSpatializer.ActiveChoreoMode = mode;
        RefreshSpatialChoreoClarifierUi();

        if (mode == SpatialChoreoMode.Off) {
            choreoBuildGeneration++;
            lastChoreoBuiltStemTrack = null;
            lastChoreoBuiltMode = SpatialChoreoMode.Off;
            lastChoreoBuiltLyricsSig = string.Empty;
            Cavern.Neural.NeuralStemSpatializer.SetChoreoTrack(null);
            playerEngine?.SceneHub?.SetChoreoTrack(null);
            playerEngine?.SyncChoreographyPositions();
            UpdateRoomCanvas();
            return;
        }

        SyncSpatialChoreoTrackIfNeeded(forceRebuild: true);
        UpdateRoomCanvas();
    }

    void SyncSpatialChoreoTrackIfNeeded(bool forceRebuild = false) {
        SyncSpatialChoreoTrackIfNeededV42(forceRebuild);
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
            double heardSec = playerEngine?.HeardSeconds ?? playerEngine?.CurrentSeconds ?? 0;
            SyncPlaybackUi(false, heardSec, playerEngine.TotalSeconds);
            mediaNotification?.UpdatePlaybackState(false, playerEngine.CurrentSeconds, showNotification: true);
        } else {
            if (playerEngine.TotalSeconds > 0 && playerEngine.CurrentSeconds >= playerEngine.TotalSeconds - 0.05) {
                playerEngine.Seek(0);
            }
            playerEngine.IsPlaying = true;
            audioOutput.Start();
            double heardSec = playerEngine?.HeardSeconds ?? playerEngine?.CurrentSeconds ?? 0;
            SyncPlaybackUi(true, heardSec, playerEngine.TotalSeconds);
            mediaNotification?.UpdatePlaybackState(true, playerEngine.CurrentSeconds, showNotification: true);
        }
    }

    void StopPlayback() {
        StopStudioPerformanceRecording(autoSelectInExport: false);
        if (playerEngine != null) {
            playerEngine.SetPerformancePreviewTake(null, enabled: false);
            playerEngine.IsPlaying = false;
            playerEngine.Seek(0);
        }
        audioOutput?.Stop();
        musicVideoStage?.SeekTo(0);
        musicVideoStage?.SyncPlaybackState(false, 0);
        mediaNotification?.UpdatePlaybackState(false, 0, showNotification: false);
        SyncPlaybackUi(false, 0, playerEngine?.TotalSeconds ?? 0);
        RefreshStudioRecorderUi();
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
            int curVolPct = GetCurrentMasterVolumePercent();
            roomMiniVolChip.Text = string.Format(S["RoomMiniVolFmt"], curVolPct);
            roomMiniVolChip.Background = isMiniVolExpanded
                ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(ColAccent, 78), OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(ColAccent, 185))
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            if (roomMiniVolSeekBar != null && roomMiniVolSeekBar.Progress != curVolPct) {
                roomMiniVolSeekBar.Progress = curVolPct;
                roomMiniVolSeekBar.Invalidate();
            }
        }
        SyncRoomMiniSpatialChipUi(roomMiniHrtfChip);
        RefreshSignalChainBarUi();
    }

    void SyncPlaybackUi(bool isPlaying, double curSeconds, double totSeconds) {
        RefreshMiniPlayerPlaybackUi(isPlaying, curSeconds, totSeconds);
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
        if (roomView == null || playerEngine == null) return;
        roomView.UpdateState(trackSourceItems, playerEngine.EnvironmentSize, playerEngine.ListenerPosition, playerEngine.ListenerRotation, playerEngine.IsPlaying);
        SyncEarCandyToRoomCanvas();
        roomView.UpdateXRayState(playerEngine.SceneHub.XRayPowermap, playerEngine.SceneHub.Telemetry);
        UpdateRoomTelemetryText();
        UpdateMixerRowsLive();
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
                TrackSourceItem live = ResolveLiveSourceItem(item);
                if (live == null) {
                    RefreshPlayerSourcesList();
                    return;
                }
                IReadOnlyList<TrackSourceItem> liveItems = trackSourceItems;
                if (playerEngine != null && playerEngine.DynamicCeilingOnly) {
                    playerEngine.DynamicCeilingOnly = false;
                    ceilingActiveSinceMs.Clear();
                    ceilingPinUntilMs.Clear();
                    for (int k = 0; k < liveItems.Count; k++) {
                        liveItems[k].IsDynamicallyFilteredOut = false;
                    }
                }
                live.IsSolo = !live.IsSolo;
                SyncMixerRowsVisualAndOrder(animate: true, forceImmediate: true);
                RefreshFilterButtonsUi();
                refreshBentoTilesUi?.Invoke();
                UpdateRoomCanvas();
            };
            muteBtn.Click += (_, _) => {
                TrackSourceItem live = ResolveLiveSourceItem(item);
                if (live == null) {
                    RefreshPlayerSourcesList();
                    return;
                }
                live.IsMuted = !live.IsMuted;
                if (!ReferenceEquals(live, item)) {
                    RefreshPlayerSourcesList();
                } else {
                    ApplyMixerRowVisualState(rowRef, playerEngine?.DynamicCeilingOnly ?? false, forceRedraw: true);
                }
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

    Java.Lang.Runnable telemetryTickRunnable;
    bool isTelemetryTimerActive;

    void StartTelemetryTimer() {
        int loudnessMeterTick = 0;
        telemetryTickRunnable ??= new Java.Lang.Runnable(() => {
            if (uiDestroyed || !isActivityResumed) {
                isTelemetryTimerActive = false;
                return;
            }
            if (playerEngine != null && playerEngine.IsPlaying) {
                double heardSec = playerEngine?.HeardSeconds ?? playerEngine?.CurrentSeconds ?? 0;
                SyncPlaybackUi(true, heardSec, playerEngine.TotalSeconds);
                if ((++loudnessMeterTick & 3) == 0) {
                    RefreshLiveLoudnessMeterUi();
                    RefreshLgsrUi();
                    if (isNowPlayingSheetOpen && activeNowPlayingSubView == 2 && store.RoomXRayInspectorExpanded) {
                        RefreshSpatialXRayHud();
                    }
                }
            }
            if (!uiDestroyed && isActivityResumed && isTelemetryTimerActive) {
                uiHandler?.PostDelayed(telemetryTickRunnable, 65);
            }
        });
        ResumeTelemetryTimer();
    }

    void ResumeTelemetryTimer() {
        if (uiDestroyed || telemetryTickRunnable == null || isTelemetryTimerActive) {
            return;
        }
        isTelemetryTimerActive = true;
        uiHandler?.RemoveCallbacks(telemetryTickRunnable);
        uiHandler?.PostDelayed(telemetryTickRunnable, 65);
    }

    void PauseTelemetryTimer() {
        isTelemetryTimerActive = false;
        if (telemetryTickRunnable != null) {
            uiHandler?.RemoveCallbacks(telemetryTickRunnable);
        }
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
