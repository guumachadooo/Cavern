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

## 4. Referências de Player, Switch, Sliders & Vídeo (`Apple Music`, `SamsungGallery2018.apk`, `SystemUI.apk`, `SamsungVideoPlayer.apk`)
- **Mecanismo de Arrasto Relativo do Slider de Brilho (`SystemUI.apk` `ToggleSeekBar.onTouchEvent` @ `classes3.dex`):**
  - Ao tocar (`ACTION_DOWN`), registra `touchedX` e `currentProgress` **sem saltar** a bola ou progresso para a coordenada de toque; em `ACTION_MOVE` após ultrapassar `scaledTouchSlop`, desloca proporcionalmente `(x - touchedX) / usableWidth * max` mantendo a interação precisa sem saltos acidentais.
- **`SeslSwitch` (`SamsungGallery2018.apk` `sesl_switch_*`):**
  - Trilha em cápsula `32dp × 20dp` (`cornerRadius = 10dp`, cor `#636368` desligado / `Primary` ligado); thumb de `20dp` com borda `2dp` (`#8F8F8F` desligado / `Primary` ligado) e núcleo `#FCFCFF` (`16dp`) que comprime para `~0.80` ao pressionar.
- **Apple Music (`music_player.xml`, `constraint_player_media_controls.xml`, `fragment_player_lyrics_sheet.xml`, `lyrics_header.xml`, `lyrics_line.xml`, `lyrics_line_instrumental.xml`):**
  - Arte `1:1` arredondada com escala dinâmica play/pause, títulos alinhados à esquerda + botão circular `•••` + badges inline (`Dolby Atmos`), transporte limpo sem bordas, barra de volume fina com ícones de alto-falante, e barra inferior de 3 ícones (balão de letras à esquerda, rota/áudio espacial ao centro, repetição/fila à direita).
  - No modo Letras: cabeçalho `lyrics_header` (`52dp` mini-capa + título/artista + `•••`), lista de letras sem bordas/card ocupando toda a largura sobre o fundo borrado, e os 3 pontos `• • •` de introdução/interlúdio (`lyrics_line_instrumental.xml`) como **linha real na lista vertical** para rolar naturalmente sem espremer frases.
- **SamsungVideoPlayer (`videoplayer_titlelayout`, `videoplayer_controllayout`, `player_controller_button_normal`) + YouTube:**
  - Barra superior (título, CC/legendas, faixa de áudio, proporção Fit/Fill/Stretch), transporte central (`-10s`, `Play/Pause`, `+10s`), duplo toque lateral `±10s` com bloom circular, barra inferior com trava de tela (`Lock`), loop e tela cheia imersiva horizontal (`Fullscreen`).

## 5. Barra de Mídia com Ondas (`WaveSeekbar` — `SystemUI.apk`)
- `WaveSeekbarProgressDrawable` + `MultiWaveAreaTrackRenderer`: ondas coloridas translúcidas animadas em reprodução, linha plana em pausa e intervalo de `8dp` após o polegar.

## Referências Extraídas de APKs Analisados (v2.8.x)
- **Apple Music 7.0.0 (`base.apk`):** Fonte oficial `res/font/sf_pro_android_ui.ttf` (eixo variável `'wght' 700` para título/letras), interpolação `PathInterpolator(0.16f, 1f, 0.3f, 1f)`, escala ancorada à esquerda `0.945 -> 1.01`, blur de profundidade (`3dp -> 0dp`) pré-escalado a 0.5x fora da GPU para compatibilidade total em canvas hardware-accelerated, além de deslocamento escalonado entre linhas durante o scroll automático.
- **Samsung Music 16.2.46 + Poweramp build 1035:** Estrutura de biblioteca `Álbuns` (grade 2 colunas com drilldown para faixas numeradas e duração), `Faixas` (badge de codec), `Artistas` e `Pastas` combinando `MediaStore` (`MediaStore.Audio`/`Video`) com varredura direta de diretórios (`Music`, `Download`, `Movies`, `Audiobooks`).
- **Kodi 22 (`metadata.generic.albums` / `tmdbapi.py`) + Poweramp (`LyricsChain2`):** Pipelines de scrapers para capas e metadados (MusicBrainz + CoverArtArchive, TheAudioDB, iTunes Search API HD `600x600`, TMDb), letras sincronizadas LRC (LRCLIB API) e legendas `.srt` (Podnapisi XML + extração ZIP).
