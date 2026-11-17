# Referência Técnica One UI 9 (Inspecionada do Dispositivo)

Resumo técnico obtido a partir de capturas nativas do sistema One UI 9 (Settings, Voice Recorder, SmartThings, Lock Screen) e inspeção de classes em `/system/system_ext/priv-app/SystemUI/SystemUI.apk` no aparelho do usuário.

## 1. Sliders de Brilho / Volume / SoundCraft (`SystemUI.apk`)
Classes de referência observadas no disassembly de `classes.dex` / `classes4.dex` / `classes5.dex`:
- `SecRoundedCornerSeekBarDrawable`, `SoundCraftRoundedCornerSeekBarDrawable`, `SoundCraftVolumeSeekBar`, `SliderTokens_phoneKt`.
- **Geometria e física exata ao tocar/arrastar:**
  - Pista em cápsula de altura `h` e raio `r = h / 2f`.
  - Quando o preenchimento excede a largura da bola (`fillRight >= h`): na extremidade ativa é desenhada uma **bolinha circular de raio completo `r = h / 2f`** (`Canvas.DrawCircle(fillRight - r, r, r)` ocupando 100% de cima a baixo do slider).
  - **Transição de opacidade (choque vs máximo):** no valor máximo (`100%`), `setShockColor(true)` torna o preenchimento `100%` sólido (igual à bolinha, que se integra na cor). Ao descer abaixo de `100%` e interagir, o corpo preenchido atenua suavemente a opacidade (`startColor` ~42% -> ~67% na barra, valores `0.4f..0.45f` em `SliderTokens_phoneKt`/`SecRoundedCornerSeekBarDrawable`), enquanto a bolinha na ponta permanece `100%` opaca (`alpha = 255`) emergindo com nitidez.
  - **Colisão com a parede esquerda (`fillRight < h`):** dentro do `clipPath` semicircular esquerdo `(r, r, r)`, o contorno direito achata o centro primeiro mantendo cantos arredondados superior/inferior, curva sutilmente para dentro (concavidade leve) na faixa intermediária e termina plano vertical até `0`.
  - Movimento com rastreamento contínuo da coordenada de toque em `float`, evitando degraus mesmo quando o `Max` lógico da barra é pequeno.

## 2. Barra de Mídia com Ondas (`WaveSeekbar`)
Classes de referência na `SystemUI.apk`:
- `com.android.systemui.media.colorfulwave.WaveSeekbarProgressDrawable` e `MultiWaveAreaTrackRenderer`.
- **Comportamento visual:**
  - Quando em reprodução, exibe faixas onduladas translúcidas sobrepostas com degradê fluido;
  - Quando pausado, recolhe suavemente para linha arredondada plana;
  - Mantém pequeno espaçamento visual após o polegar/thumb para a trilha restante.

## 3. Estética Nativa e Paleta Multi-Tom no Modo Escuro (One UI 9)
- Fundos de tela e superfícies principais permanecem em **preto / grafite neutro** (`#000000` / `#08080A` no fundo, cards escuros translúcidos limpos com borda especular sutil), mesmo com paletas de papel de parede quentes/vibrantes.
- Conforme `AndroidColorScheme` (`SystemUI.apk`) e `framework-res.apk`, a paleta Monet oficial da One UI expõe múltiplos subtons harmônicos:
  - `system_accent1_*` / `system_primary_dark`: acento primário principal (botões primários, aba ativa, switches ligados, destaque principal).
  - `system_accent3_*` / `system_tertiary_dark`: subtom complementar gerado do papel de parede (por exemplo, tom rosado/coral harmonizado quando `accent1` é laranja), aplicado naturalmente em sliders, realces secundários e contraste tonal.
  - `system_accent2_*` / `system_secondary_dark`: suporte tonal secundário para eixos auxiliares, chips e controles secundários.
