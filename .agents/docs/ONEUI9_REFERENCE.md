# Referência Técnica One UI 9 (Inspecionada dos APKs do Dispositivo)

Resumo técnico exato extraído diretamente dos APKs do aparelho (`/system_ext/priv-app/SystemUI/SystemUI.apk`, `/system/framework/framework-res.apk`, `/system/priv-app/SamsungGallery2018/SamsungGallery2018.apk`, `/system/priv-app/SamsungVideoPlayer/SamsungVideoPlayer.apk`) e do `/sdcard/Download/com.apple.android.music*.apkm` / `YouTube.apk`.

## 1. Mapeamento Oficial de Paleta Dinâmica (`framework-res.apk` + `SystemUI.apk`)
Fonte: `res/raw/wallpapertheme_template.json`, `wallpapertheme_template_v2_A.json`, `wallpapertheme_template_v2_B.json` (`framework-res.apk`) cruzados com `res/xml/metadata_colorpalette.xml` e `res/xml/resource_map.xml` (`SystemUI.apk`):
- **`quickpanel_btn_on` (`UID="4-2-3"`, `qs_tile_round_background_on`) / `primary`**: `@android:color/system_accent1_300` (Light & Dark); `primary_dark` = `@android:color/system_accent1_200` no Dark (`accent1_600` no Light).
- **`quickpanel_progress` (`UID="4-8-5"`, `tw_progress_color_control_activated`, `soundcraft_volume_progress_color_control`)**: **`@android:color/system_accent2_300`** (Light & Dark) — **este é o tom exato usado no slider da barra de controle / painel de controle** (não `system_accent3_300`!).
- **`secondary` / `single_track`**: `@android:color/system_accent2_500`; `notification_header_color` = `@android:color/system_accent2_400`.
- **`highlight`**: `@android:color/system_accent3_300` no Dark (`accent3_600` no Light).
- **`quickpanel_icon`**: `#FAFAFF` em Light/Dark.

## 2. Sliders de Brilho / Volume / SoundCraft (`SystemUI.apk`)
Classes e XMLs verificados: `SecBrightnessSliderController` (`updateSliderDrawable`, `thumbAnimator$1$2`), `SecBrightnessSliderView` (`setDualSeekBarResources`), `SecRoundedCornerSeekBarDrawable`, `res/drawable/sec_brightness_progress_gradient_drawable.xml`, `res/color/tw_progress_color_control_activated_start.xml`, `tw_progress_color_control_activated_end.xml`, `tw_progress_color_thumb.xml`.
- **Repouso (`isThumbShowing == false`)**:
  - A barra preenchida (`0..fillRight`) usa **cor sólida 100% uniforme (`tw_progress_color_control_activated` = `system_accent2_300`, `alpha = 255`)** do mínimo ao máximo;
  - A bolinha (`thumb`) fica **totalmente invisível (`alpha = 0` / drawable transparente `0x7e081158`)**.
- **Durante interação (`isThumbShowing == true`)**:
  - `TransitionDrawable` (`250ms`): reduz o início da barra para `tw_progress_color_control_activated_start.xml` (`alpha = 0.50f` do mesmo `0x7e060ef0`), mantendo `tw_progress_color_control_activated_end.xml` (`alpha = 1.0f`) e o `thumb` (`tw_progress_color_thumb.xml` = mesmo `0x7e060ef0`, `alpha = 1.0f`, diâmetro `= h`, `radius = h / 2`).
  - Próximo a 100% (`setShockColor(true)` / `progress >= threshold`), volta ao estado sólido uniforme (`endColor`).
  - Na parede esquerda (`fillRight < h`), usa recorte pela cápsula externa (`clipPath` semicircular) sobre `drawRoundRect(..., fillRight * 0.5f)` sem curva côncava explícita.

## 3. Barra de Navegação Inferior Flutuante (`SamsungGallery2018.apk`)
Recursos decodificados: `res/ii.xml` (`menu_bottom_tab`), `res/Ll1.xml` (`sesl_bottom_navigation_item`), `res/DX.xml` (`sesl_floating_appbar_round_background_dark`), `res/r6.xml` (`sesl_tablayout_maintab_item_background_dark`), ícones `res/5R.xml`/`res/O5.xml` (Pictures), `res/T5.xml`/`res/KZ.xml` (Albums), `res/H6.xml`/`res/n01.xml` (Stories):
- **Cápsula flutuante**: altura `56dp` (`0x38`), `radius = 999dp` (`sesl_figma_floating_component_background_radius`), fundo `basic_token_sys_surface_brightest_dark` (`#3A3A3D`) com alpha `0.90f` (`sesl_figma_non_blur_common_small_alpha_dark = 0.9`).
- **Indicador de aba selecionada**: pílula interna sutil **`basic_token_state_selected_on_surface_dark = 0x1AFFFFFF`** (`#FFFFFF` a ~10% de opacidade) — **nunca um bloco sólido laranja/azul**.
- **Item da aba (`sesl_bottom_navigation_item`)**: ícone `24dp` em cima + label `10sp–10.5sp` embaixo (`singleLine`). Ícone alterna entre traço vazado/semi-transparente (`_line`) no estado inativo e preenchido com recortes internos (`_solid`) no estado selecionado.

## 4. Referências de Player & Letras (`Apple Music`, `YouTube.apk`, `SamsungVideoPlayer.apk`)
- **Apple Music (`music_player.xml`, `fragment_player_lyrics_sheet.xml`, `lyrics_header.xml`, `lyrics_line.xml`)**:
  - Arte `1:1` arredondada (`corner_radius_l`, elevação `15dp`, margens `24dp`), títulos alinhados à esquerda com badges inline (`Dolby Atmos` / `Lossless`), linha ativa da letra destacada em branco vivo (`24sp Bold`), linhas inativas em `alpha = 0.35f`, pontos pulsantes para interlúdios (`lyrics_instrumental_dot`), cabeçalho compacto (`60dp` thumb + título/artista) quando a letra expande, gradiente alfa superior/inferior no `RecyclerView` das letras.
- **YouTube (`res/zmf.xml`, `res/FZD.xml`) + SamsungVideoPlayer (`res/Nc.xml`, `res/Cs.xml`)**:
  - Gestos em vídeo: toque simples alterna controles overlay (`Play/Pause`, `-10s`/`+10s`, chip de velocidade); duplo toque na esquerda/direita busca `-10s`/`+10s` com bloom circular (`CircularClipTapBloomView`).

## 5. Barra de Mídia com Ondas (`WaveSeekbar` — `SystemUI.apk`)
- `WaveSeekbarProgressDrawable` + `MultiWaveAreaTrackRenderer`: ondas coloridas translúcidas animadas em reprodução, linha plana em pausa e intervalo de `8dp` após o polegar.
