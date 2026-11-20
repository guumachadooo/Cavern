# One UI 9 & Reference App Technical Specs (`ONEUI9_REFERENCE.md`)

> Exact resource tokens, dimensions, animations, and architectural findings extracted read-only from device system APKs (`/system_ext/priv-app/SystemUI/SystemUI.apk`, `/system/framework/framework-res.apk`, `/system/priv-app/SamsungGallery2018/SamsungGallery2018.apk`, `/system/priv-app/SamsungVideoPlayer/SamsungVideoPlayer.apk`) and analyzed reference APKs (`Apple Music 7.0.0`, `Samsung Music 16.2.46`, `Poweramp build 1035`, `Kodi 22`, `YouTube.apk`).
>
> **Maintenance Rule:** Update the matching component section **in place** whenever new measurements or APK specs are discovered — do not append conflicting version dumps at the bottom.

---

## 1. Official Dynamic Color Palette (`framework-res.apk` + `SystemUI.apk`)
Sources: `res/raw/wallpapertheme_template.json`, `wallpapertheme_template_v2_A.json`, `wallpapertheme_template_v2_B.json` (`framework-res.apk`) cross-referenced with `res/xml/metadata_colorpalette.xml` and `res/xml/resource_map.xml` (`SystemUI.apk`):
- **`quickpanel_btn_on` (`UID="4-2-3"`, `qs_tile_round_background_on`) / `primary`:** `@android:color/system_accent1_300` (Light & Dark); `primary_dark` = `@android:color/system_accent1_200` in Dark (`accent1_600` in Light).
- **`quickpanel_progress` (`UID="4-8-5"`, `tw_progress_color_control_activated`, `soundcraft_volume_progress_color_control`):** **`@android:color/system_accent2_300`** (Light & Dark) — exact control slider accent used in Quick Panel / SoundCraft (**not** `system_accent3_300`).
- **`secondary` / `single_track`:** `@android:color/system_accent2_500`; `notification_header_color` = `@android:color/system_accent2_400`.
- **`highlight`:** `@android:color/system_accent3_300` in Dark (`accent3_600` in Light).
- **`quickpanel_icon`:** `#FAFAFF` in both Light & Dark.

## 2. Brightness, Volume & SoundCraft Sliders (`SystemUI.apk`)
Inspected classes/XMLs: `SecBrightnessSliderController` (`updateSliderDrawable`, `thumbAnimator$1$2`), `SecBrightnessSliderView` (`setDualSeekBarResources`), `SecRoundedCornerSeekBarDrawable`, `res/drawable/sec_brightness_progress_gradient_drawable.xml`, `res/color/tw_progress_color_control_activated_start.xml`, `tw_progress_color_control_activated_end.xml`, `tw_progress_color_thumb.xml`, and `ToggleSeekBar.onTouchEvent` (`classes3.dex`).
- **Idle state (`isThumbShowing == false`):**
  - Filled track (`0..fillRight`) uses **100% uniform solid color** (`tw_progress_color_control_activated` = `system_accent2_300`, `alpha = 255`) from min to max.
  - Thumb is **completely invisible** (`alpha = 0` / transparent drawable `0x7e081158`).
- **Active drag (`isThumbShowing == true`):**
  - `TransitionDrawable` (`250ms`) fades track start to `tw_progress_color_control_activated_start.xml` (`alpha = 0.50f` of `0x7e060ef0`), keeping `tw_progress_color_control_activated_end.xml` (`alpha = 1.0f`) and visible `thumb` (`tw_progress_color_thumb.xml` = `0x7e060ef0`, `alpha = 1.0f`, diameter `= h`, `radius = h / 2`).
  - Near 100% (`setShockColor(true)` / `progress >= threshold`), blends back to solid uniform `endColor`.
  - Left wall clamping (`fillRight < h`) uses outer semicircular capsule `clipPath` over `drawRoundRect(..., fillRight * 0.5f)` without concave deformation.
- **Relative Touch Drag Physics (`ToggleSeekBar`):**
  - On `ACTION_DOWN`, records `touchedX` and `currentProgress` **without jumping** thumb/progress to the tap coordinate; on `ACTION_MOVE` past `scaledTouchSlop`, applies relative delta `(x - touchedX) / usableWidth * max` for smooth precision without accidental jumps.

## 3. Floating Bottom Navigation & Switches (`SamsungGallery2018.apk`)
Decoded resources: `res/ii.xml` (`menu_bottom_tab`), `res/Ll1.xml` (`sesl_bottom_navigation_item`), `res/DX.xml` (`sesl_floating_appbar_round_background_dark`), `res/r6.xml` (`sesl_tablayout_maintab_item_background_dark`), icons `res/5R.xml`/`res/O5.xml` (Pictures), `res/T5.xml`/`res/KZ.xml` (Albums), `res/H6.xml`/`res/n01.xml` (Stories), `sesl_switch_*`:
- **Floating capsule:** Height `56dp` (`0x38`), `radius = 999dp` (`sesl_figma_floating_component_background_radius`), surface `basic_token_sys_surface_brightest_dark` (`#3A3A3D`) at `0.90f` alpha (`sesl_figma_non_blur_common_small_alpha_dark = 0.9`).
- **Selected tab pill:** Subtle translucent highlight **`basic_token_state_selected_on_surface_dark = 0x1AFFFFFF`** (~10% white) — never a solid orange/blue block.
- **Tab item (`sesl_bottom_navigation_item`):** `24dp` icon above `10sp–10.5sp` (`singleLine`) label; toggles between outlined/translucent stroke (`_line`) when inactive and filled cutout (`_solid`) when selected.
- **`SeslSwitch` (`sesl_switch_*`):** `32dp × 20dp` capsule track (`cornerRadius = 10dp`, `#636368` off / `Primary` on); `20dp` thumb with `2dp` ring (`#8F8F8F` off / `Primary` on) and `#FCFCFF` (`16dp`) inner core compressing to `~0.80` on press.

## 4. Media Seekbar (`WaveSeekbar` — `SystemUI.apk`)
- `WaveSeekbarProgressDrawable` + `MultiWaveAreaTrackRenderer`: animated translucent colored wave while playing, flat line when paused, and an `8dp` gap after the thumb.

## 5. Apple Music Player & Lyrics (`com.apple.android.music` 7.0.0)
Inspected layouts & assets: `music_player.xml`, `constraint_player_media_controls.xml`, `fragment_player_lyrics_sheet.xml`, `lyrics_header.xml`, `lyrics_line.xml`, `lyrics_line_instrumental.xml`, `res/font/sf_pro_android_ui.ttf`:
- **Typography & Motion:** Variable font `sf_pro_android_ui.ttf` (`'wght' 700` for titles/lyrics), `PathInterpolator(0.16f, 1f, 0.3f, 1f)`, left-anchored active line scale `0.945 -> 1.01`, depth blur (`3dp -> 0dp`) pre-scaled at `0.5x` offscreen for reliable hardware-accelerated canvas rendering, plus staggered line offsets during auto-scroll.
- **Now Playing Layout:** Rounded `1:1` artwork with dynamic play/pause scale, left-aligned title/artist + circular `•••` menu button + inline badges (`Dolby Atmos`), borderless transport controls, slim volume bar with speaker icons, and 3-action footer (lyrics bubble, audio/spatial route, repeat/queue).
- **Lyrics Mode:** Compact `lyrics_header` (`52dp` mini-art + title/artist + `•••`), edge-to-edge borderless lyrics canvas over blurred backdrop, and intro/interlude dots `• • •` (`lyrics_line_instrumental.xml`) modeled as a **real row in the vertical scroll list** so scrolling stays natural without crowding adjacent lines.

## 6. Video HUD (`SamsungVideoPlayer.apk` + `YouTube.apk`)
- `videoplayer_titlelayout`, `videoplayer_controllayout`, `player_controller_button_normal`: Top bar (title, CC/subtitles, audio track selector, Fit/Fill/Stretch aspect ratio), center transport (`-10s`, `Play/Pause`, `+10s`), left/right double-tap `±10s` with circular bloom, and bottom bar with screen touch lock, loop toggle, and immersive landscape fullscreen.

## 7. Media Library & Online Metadata Pipelines (`Samsung Music 16.2.46`, `Poweramp build 1035`, `Kodi 22`)
- **Library Structure:** `Albums` (2-column grid drilling into track-numbered rows with duration), `Tracks` (with codec badge), `Artists`, and `Folders`, combining `MediaStore` (`MediaStore.Audio`/`Video`) with direct directory scanning (`Music`, `Download`, `Movies`, `Audiobooks`).
- **Online Scrapers:** Cover art & album metadata (`MusicBrainz + CoverArtArchive`, `TheAudioDB`, `iTunes Search API` HD `600x600`, `TMDb`), synchronized `.lrc` lyrics (`LRCLIB API`, `Netease`, `Lyrics.ovh`), and `.srt` subtitles (`Podnapisi` XML + ZIP extraction, `OpenSubtitles` REST).
