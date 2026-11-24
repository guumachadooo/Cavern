# One UI 9 & Reference App Specs (`ONEUI9_REFERENCE.md`)

> Exact tokens/dimensions/animations from read-only APK inspection (`/system_ext/priv-app/SystemUI/SystemUI.apk`, `/system/framework/framework-res.apk`, `/system/priv-app/SamsungGallery2018/SamsungGallery2018.apk`, `/system/priv-app/SamsungVideoPlayer/SamsungVideoPlayer.apk`, `Apple Music 7.0.0`, `Samsung Music 16.2.46`, `Poweramp build 1035`, `Kodi 22`, `YouTube.apk`).
> RULE|update matching section in-place (`!append conflicting version dumps`).
> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, verbatim resource tokens/dims/colors).

---

## 1. Official Dynamic Color Palette (`framework-res.apk` + `SystemUI.apk`)
- Sources: `res/raw/wallpapertheme_template.json`, `wallpapertheme_template_v2_A.json`, `wallpapertheme_template_v2_B.json` + `res/xml/metadata_colorpalette.xml`, `resource_map.xml`.
- `quickpanel_btn_on` (`UID="4-2-3"`, `qs_tile_round_background_on`) / `primary` -> `@android:color/system_accent1_300` (Light & Dark); `primary_dark` -> `@android:color/system_accent1_200` (Dark) | `accent1_600` (Light).
- `quickpanel_progress` (`UID="4-8-5"`, `tw_progress_color_control_activated`, `soundcraft_volume_progress_color_control`) -> **`@android:color/system_accent2_300`** (Light & Dark; NOT `system_accent3_300`).
- `secondary` / `single_track` -> `@android:color/system_accent2_500`; `notification_header_color` -> `@android:color/system_accent2_400`.
- `highlight` -> `@android:color/system_accent3_300` (Dark) | `accent3_600` (Light); `quickpanel_icon` -> `#FAFAFF` (Light & Dark).

## 2. Brightness, Volume & SoundCraft Sliders (`SystemUI.apk`)
- Sources: `SecBrightnessSliderController` (`updateSliderDrawable`, `thumbAnimator$1$2`), `SecBrightnessSliderView` (`setDualSeekBarResources`), `SecRoundedCornerSeekBarDrawable`, `sec_brightness_progress_gradient_drawable.xml`, `tw_progress_color_control_activated_start.xml`, `tw_progress_color_control_activated_end.xml`, `tw_progress_color_thumb.xml`, `ToggleSeekBar.onTouchEvent` (`classes3.dex`).
- Idle (`!isThumbShowing`): filled track `0..fillRight` 100% uniform solid `tw_progress_color_control_activated` (`system_accent2_300`, `alpha=255`); thumb invisible (`alpha=0` / `0x7e081158`).
- Drag (`isThumbShowing`): `TransitionDrawable` (`250ms`) fades start -> `tw_progress_color_control_activated_start.xml` (`alpha=0.50f` of `0x7e060ef0`), end -> `tw_progress_color_control_activated_end.xml` (`alpha=1.0f`), thumb -> `tw_progress_color_thumb.xml` (`0x7e060ef0`, `alpha=1.0f`, `diam=h`, `r=h/2`); near 100% (`setShockColor(true)`) blends back to solid `endColor`.
- Left wall clamp (`fillRight < h`): outer semicircular capsule `clipPath` over `drawRoundRect(..., fillRight * 0.5f)` (`!concave bend`).
- `ToggleSeekBar` physics: `ACTION_DOWN` records `touchedX` + `currentProgress` w/o jump; `ACTION_MOVE > scaledTouchSlop` applies relative `(x - touchedX) / usableWidth * max`.

## 3. Floating Bottom Nav & Switches (`SamsungGallery2018.apk`)
- Sources: `res/ii.xml`, `res/Ll1.xml`, `res/DX.xml`, `res/r6.xml`, `res/5R.xml`/`O5.xml`, `T5.xml`/`KZ.xml`, `H6.xml`/`n01.xml`, `sesl_switch_*`.
- Floating capsule: `h=56dp` (`0x38`), `radius=999dp` (`sesl_figma_floating_component_background_radius`), surface `basic_token_sys_surface_brightest_dark` (`#3A3A3D`) @ `alpha=0.90f`.
- Selected tab pill: `basic_token_state_selected_on_surface_dark = 0x1AFFFFFF` (~10% white; `!solid orange/blue`).
- Tab item (`sesl_bottom_navigation_item`): `24dp` icon over `10sp–10.5sp` (`singleLine`) label; toggles outlined `_line` <-> filled `_solid`.
- `SeslSwitch`: `32dp x 20dp` track (`cornerRadius=10dp`, `#636368` off / `Primary` on); `20dp` thumb w/ `2dp` ring (`#8F8F8F` off / `Primary` on) + `#FCFCFF` (`16dp`) core compressing `~0.80` on press.

## 4. Media Seekbar (`WaveSeekbar` — `SystemUI.apk`)
- `WaveSeekbarProgressDrawable` + `MultiWaveAreaTrackRenderer`: animated translucent wave when playing, flat line when paused, `8dp` gap after thumb.

## 5. Apple Music Player & Lyrics (`com.apple.android.music` 7.0.0)
- Sources: `music_player.xml`, `constraint_player_media_controls.xml`, `fragment_player_lyrics_sheet.xml`, `lyrics_header.xml`, `lyrics_line.xml`, `lyrics_line_instrumental.xml`, `res/font/sf_pro_android_ui.ttf`.
- Motion/Type: `sf_pro_android_ui.ttf` (`'wght' 700`), `PathInterpolator(0.16f, 1f, 0.3f, 1f)`, left-anchored active line scale `0.945 -> 1.01`, `0.5x` offscreen depth blur (`3dp -> 0dp`), staggered auto-scroll offsets.
- Now Playing: `1:1` rounded art w/ play/pause scale, left-aligned title/artist + `•••` + inline badges (`Dolby Atmos`), borderless transport, slim volume bar w/ speaker icons, 3-action footer (lyrics, audio/spatial route, repeat/queue).
- Lyrics: `lyrics_header` (`52dp` mini-art + title/artist + `•••`), edge-to-edge canvas over blur backdrop, `• • •` interludes (`lyrics_line_instrumental.xml`) as real vertical scroll row.

## 6. Video HUD (`SamsungVideoPlayer.apk` + `YouTube.apk`)
- `videoplayer_titlelayout`, `videoplayer_controllayout`, `player_controller_button_normal`: top bar (title, CC/subtitles, audio track, Fit/Fill/Stretch), center (`-10s`, `Play/Pause`, `+10s`), L/R double-tap `±10s` circular bloom, bottom bar (touch lock, loop, landscape fullscreen).

## 7. Media Library & Online Metadata (`Samsung Music 16.2.46`, `Poweramp 1035`, `Kodi 22`)
- Library: `Albums` (2-col grid -> numbered tracks + duration), `Tracks` (codec badge), `Artists`, `Folders`; `MediaStore` (`MediaStore.Audio`/`Video`) + dir scan (`Music`, `Download`, `Movies`, `Audiobooks`).
- Scrapers: art/tags (`MusicBrainz`+`CoverArtArchive`, `TheAudioDB`, `iTunes Search API` `600x600`, `TMDb`), `.lrc` (`LRCLIB`, `Netease`, `Lyrics.ovh`), `.srt` (`Podnapisi` XML+ZIP, `OpenSubtitles` REST).

## 8. Cavernize One UI 9 Design System, Settings Hub, Sing Dock & Edge-to-Edge Insets (`W26–W27`)
- **Design System Primitives (`OneUiTheme.cs` + `OneUiComponents.cs`):**
  - `CreateCategoryHubTile(ctx, icon, iconColor, title, liveSubtitle, onClick, out subtitleView)` -> `minH=68dp`, `38dp` squircle `OneUiIconBadge`, single-line ellipsized bold title + live status subtitle + `16dp` `ChevronRight`.
  - `CreateStatusChip` / `UpdateStatusChip` -> `11f` bold ellipsized pill (`PillCornerDp`, `48` fill / `145` border alpha).
  - `CreateEmptyPrereqCard` -> `18f` `SurfaceSubTile` + `FunctionalAmber` (`110` alpha) border + optional `38dp` `Primary` pill CTA.
  - `OneUiModalDialog.SetHelpAction(dialog, onHelp, contentDesc)` -> header `?` action slot; `activeBlurredDialogs` (`HashSet<Dialog>`) ref-counts API 31+ `SetRenderEffect` blur (`0` leaks on stacked modals); single-button footer centered.
  - `OneUiInlineSelector` -> single-line ellipsized pill (`44dp` minH, `Ellipsize=End`); `RoomSimulatorView` -> 2-row top chip bar (`<=320dp` safe), reused `clipRoomPath.Reset()`, `IsSolo` race guard.
- **6-Category Settings Hub (`MainActivity.SettingsTab.cs`):**
  - 6 tiles (*Playback & Spatial*, *Headphones & Calibration*, *AI & Restoration*, *Pro-Audio & Microphone*, *Engines & Storage*, *Diagnostics & About*) + `RefreshSettingsCategorySubtitles` + cross-category search (`MatchesSettingsSearch`) + Essential/Pro mode chip (`isSettingsProMode`) + `← All Settings` back-nav (`TryHandleInternalBack` / `JumpToSettingsCategory`).
  - Simplified Spatial Upmix card: unified 3-mode `upmixModeSelector` (`Direct` / `Surround 7.1` / `Full 3D`, DirAC STFT always enabled internally) + `atmosAwareUpmixSection` gated to active upmix (`MatrixUpmixing || CavernizeUpmix`); Neural Models modal exposes clickable `Open Link` + `ShowModelDownloadErrorDialog` (selectable URL, Open Link, Copy Link, Import `.onnx`, CTS cancel).
  - Guided `?` help modals (`ShowModalGuidedHelp`) across `HRTF Wizard` (draft cancel-revert via `RevertDraftIfNeeded` + tone cleanup), `AutoEq` (`180 ms` search debounce), `Neural Models & Cache` (`NeuralTrackCacheMaxMb` `256..4096 MB` + 2-row `HRow` buttons), `Auto-Vocal Mix-Match`, and `Focusrite Scarlett`.
- **5-Tab IA, 2-Tier `Lyrics Sing` & `AudioSpec [A|B]` (`MainActivity.{PlayerMusic,Player,Converter,Extractor}Tab.cs`):**
  - Play viewport compaction (`availHDp < 600` or landscape Lyrics -> `targetArtDp` clamped `150..220dp` + compact gaps); `musicLyricsSingDock` starts collapsed (`ViewStates.Gone`) on Lyrics open and expands on demand via `musicLyricsSingToggleBtn` pill (`lyricsHeader` right, `OneUiIconType.Microphone` w/ active accent/recoil & Karaoke/LiveMic status); off-UI-thread `ScheduleLibraryLoadAndRenderAsync` shows instant `LibraryLoadingCached` status while building library index/variants.
  - `ShowAudioSpecDialog`: `[A: Original | B: ATMOS+FLAC / Spatial 3D]` live A/B toggle (`RefreshAbHeaderVisuals` + callback refresh) + collapsible Pro signal telemetry (`DriftPpm`, `Coherence`, `EnvelopeLock`).
  - `3D Room` 5-pill `BuildActiveSignalChainBar` + mini transport; `Studio` `Master|Classic|Queue` + Readiness Pills + 1 CTA; `Stems` 4 Goal Cards + live AI pipeline preview.
- **Edge-to-Edge `WindowInsets` & DisplayCutout (`MainActivity.cs` + Tabs):**
  - `ConfigureEdgeToEdgeWindow` (`SetDecorFitsSystemWindows(false)`, `LayoutInDisplayCutoutMode.Always`) + `RootInsetsListener` (`SystemBars() | DisplayCutout()`, `Ime()`).
  - `rootLayoutHost` applies `insetLeft/Top/Right`; `navFrostedHost` hides when `imeOpen` (`insetImeBottomPx > insetBarsBottomPx + 48dp`) & shifts `BottomMargin = 12dp + scrollBottom` w/o double L/R cutout padding; `tabCells[4]` `WrapContent`; `ApplyTabPageCutoutPadding` adds landscape cutout margins on `Converter`/`Extractor`; `fsRoot` applies `DisplayCutout|SystemBars` to fullscreen video HUD overlay only.

## 9. One UI 9 Motion, Physics & Smart Chrome-Aware Tab Transitions (`T4`)
- **Persistent Workspace Top Chrome (`MainActivity.cs` + `MainActivity.Navigation.cs`):**
  - Single `workspaceChromeHost` (wrapping `CreateWorkspaceSourceBar()`) hoisted inside `rootLayoutHost` directly above `contentFrame` (ZERO duplicate source bars inside `PlayerTab`/`ConverterTab`/`ExtractorTab` scroll views).
  - **Chrome-Aware Tab Transition Law:** Switching among non-Play workspace tabs (`1..4`: `3D Room`, `Studio`, `Stems`, `Settings`) keeps `workspaceChromeHost` 100% stationary (`0` layout jump, `0` re-fade, `0` re-translate), animating ONLY `contentFrame` (`190 ms`, `PathInterpolator(0.22f, 1f, 0.36f, 1f)`, `14 dp` directional slide + `0.985 -> 1.0` subtle scale).
  - **Play Boundary Transition (`0 <-> 1..4`):** `UpdateWorkspaceChromeVisibility(show, animate: crossingPlayBoundary)` animates `workspaceChromeHost` strictly when entering/leaving Play (`0`): show (`190 ms`, `PathInterpolator(0.22f, 1f, 0.36f, 1f)`, `TranslationY -16dp -> 0`, `Alpha 0 -> 1`) and hide (`150 ms`, `PathInterpolator(0.4f, 0f, 1f, 1f)`, `TranslationY 0 -> -14dp`, `Alpha 1 -> 0` + auto-collapse `fileDetailsExpanded`), guarded by monotonic `workspaceChromeAnimGen` so rapid tab switches never leak stale `WithEndAction`.
- **Play-Tab Spatial Hierarchy & Universal Inline `(i)` Info Sheets (`MainActivity.{UiHelpers,PlayerMusic.Core,NeuralWorkflows,Settings.*}.cs`):**
  - Play bottom spatial bar consolidated to 3 orthogonal pills + inline `(i)` guide: `Upmix` (`Direct` / `Matrix 7.1` / `Cavernize 3D`) + `HRTF 3D` (headphone binaural output) + `AI 3D` (neural stem separation) + `OneUiIconType.Info` help button explaining full signal chain.
  - `3D Room` explicitly separates **Processing Tier** (`Direct` / `Matrix 7.1` / `Cavernize 3D` / `AI Stems 3D` / `AI 7.1.4 Hybrid` / `Native Atmos` / `Atmos + AI Twin`) from **Motion Choreography Profile** (`Off` / `Subtle` / `Musical` / `Cinematic` / `Orbit`) via `RefreshSpatialChoreoClarifierUi`.
  - Universal `CreateInlineHelpBtn` (`OneUiIconType.Info`, `26 dp` tap target, subtle border, localized `OkBtn`) wired to non-self-explanatory AI/DSP selectors and toggles across Play, 3D Room, and Settings.
- **3D Room Direct Orbit Drag (`RoomSimulatorView.cs` + `AppSettingsStore.cs`):**
  - Default (`RoomInvertOrbitDrag = false`) uses finger-following direct manipulation (`ComputeOrbitDelta`: `sign = invertDrag ? -1f : 1f`, `+stepX -> +camYawDeg` moving near room wall right with finger); optional persisted invert toggle exposed in Settings (Category 0) and 3D Room Fine-Tune panel.
- **Synchronized Lyrics Word Illumination (`MediaStageViews.cs` + `AppSettingsStore.cs`):**
  - `LyricsWordHighlightStyle` (`SmoothGlow = 0` default, `MinimalClean = 1`, `SoftWordPill = 2`) persisted via `LyricsHighlightStyleIndex`.
  - Default `SmoothGlow` draws **0** white word-box/pill rectangles and allocates **0 B/frame** inside `DrawSyllableSyncedLine`: uses `ComputeSmoothWordProgress` Hermite sweep, 3-band feathered luminance edge (`<=18dp` sweep), and subtle `<=1.4 dp` sinusoidal `ComputeActiveWordBellEnvelope` micro-lift; opt-in `SoftWordPill` modulates pill alpha smoothly via the bell envelope.

