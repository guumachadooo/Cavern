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
