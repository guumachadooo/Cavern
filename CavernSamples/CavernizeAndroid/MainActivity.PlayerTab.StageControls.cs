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
        titleTv.SetSingleLine(false);
        titleTv.SetMaxLines(2);
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
            if (!useLiveEngineSources) {
                IReadOnlyList<TrackSourceItem> items = trackSourceItems;
                for (int i = 0; i < items.Count; i++) {
                    items[i].ResetPosition();
                }
            }
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
            b.SetTextColor(on ? OneUiTheme.GetContrastingTextColor(accents[i]) : ColText);
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
            xrayLgsrBypassBtn.SetTextColor(byp ? OneUiTheme.GetContrastingTextColor(ColDanger) : ColText);
        }
        if (xrayVocalBypassBtn != null) {
            bool byp = playerEngine.VocalChainBypassed;
            xrayVocalBypassBtn.Text = $"{S["XRayVocalBypassBtn"]}: {(byp ? S["XRayBypassActive"] : S["XRayActiveLive"])}";
            xrayVocalBypassBtn.Background = byp
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColDanger, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            xrayVocalBypassBtn.SetTextColor(byp ? OneUiTheme.GetContrastingTextColor(ColDanger) : ColText);
        }
        RefreshSignalChainBarUi();
    }

    void UpdateRoomLockButtonUi() {
        if (roomLockBtn == null) {
            return;
        }
        bool locked = store.RoomPositionLocked;
        Color lockInk = locked ? OneUiTheme.OnPrimary : ColText;
        roomLockIcon.IconType = locked ? OneUiIconType.Lock : OneUiIconType.Unlock;
        roomLockIcon.IconColor = lockInk;
        roomLockText.Text = locked ? S["UnlockRoom"] : S["LockRoom"];
        roomLockText.SetTextColor(lockInk);
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
        if (stage3dScrollView is not ScrollView roomScroll) {
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
        if (stage3dScrollView is ScrollView roomScroll) {
            roomScrollMixerHooked = true;
            roomScroll.ScrollChange += (_, _) => {
                if (pendingMixerReorder && isNowPlayingSheetOpen && activeNowPlayingSubView == 2 && IsMixerTopVisibleInViewport()) {
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
        IReadOnlyList<TrackSourceItem> liveItems = trackSourceItems;
        bool staleRefs = mixerRowRefs.Count != liveItems.Count;
        if (!staleRefs) {
            for (int i = 0; i < mixerRowRefs.Count; i++) {
                if (!ReferenceEquals(mixerRowRefs[i].Item, liveItems[i])) {
                    staleRefs = true;
                    break;
                }
            }
        }
        if (staleRefs) {
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

    TrackSourceItem ResolveLiveSourceItem(TrackSourceItem item) {
        if (item == null) {
            return null;
        }
        IReadOnlyList<TrackSourceItem> live = trackSourceItems;
        for (int i = 0; i < live.Count; i++) {
            if (ReferenceEquals(live[i], item)) {
                return live[i];
            }
        }
        if (!string.IsNullOrEmpty(item.StableKey)) {
            for (int i = 0; i < live.Count; i++) {
                if (string.Equals(live[i].StableKey, item.StableKey, StringComparison.Ordinal)) {
                    return live[i];
                }
            }
        }
        if (!string.IsNullOrEmpty(item.Name)) {
            for (int i = 0; i < live.Count; i++) {
                if (string.Equals(live[i].Name, item.Name, StringComparison.OrdinalIgnoreCase)) {
                    return live[i];
                }
            }
        }
        return item.Index >= 0 && item.Index < live.Count ? live[item.Index] : null;
    }

    void UpdateMixerRowsLive() {
        if (mixerRowRefs.Count == 0 || !isNowPlayingSheetOpen || activeNowPlayingSubView != 2) {
            return;
        }
        IReadOnlyList<TrackSourceItem> liveItems = trackSourceItems;
        if (mixerRowRefs.Count != liveItems.Count || !ReferenceEquals(mixerRowRefs[0].Item, liveItems[0])) {
            RefreshPlayerSourcesList();
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
}
