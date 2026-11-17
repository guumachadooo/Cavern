# Referência Técnica One UI 9 (Inspecionada do Dispositivo)

Resumo técnico obtido a partir de capturas nativas do sistema One UI 9 (Settings, Voice Recorder, SmartThings, Lock Screen) e inspeção de classes em `/system/system_ext/priv-app/SystemUI/SystemUI.apk` no aparelho do usuário.

## 1. Sliders de Brilho / Volume / SoundCraft (`SystemUI.apk`)
Classes de referência observadas no disassembly de `classes.dex` / `classes3.dex` / `classes4.dex`:
- `SoundCraftRoundedCornerSeekBarDrawable`, `SoundCraftVolumeSeekBar`, `SoundCraftVolumeMotion`, `BrightnessProgressDrawable`, `BidirectionalBarKt`, `IconProgressBarKt`.
- **Comportamento visual ao tocar (`isTracking`):**
  - Pista em formato de cápsula arredondada com recorte limpo nas bordas.
  - Dentro da extremidade ativa do preenchimento aparece uma **"bolinha" circular (`buttonColor` via `Canvas.DrawCircle`)** animada quando o usuário toca ou arrasta (`buttonAnimator`, `PathInterpolator(0.22f, 0.25f, 0.0f, 1.0f)`), recolhendo suavemente ao soltar.
  - Movimento com física de mola (`SpringAnimation` / `SpringForce` com `dampingRatio = 1.0f`, `stiffness = 450.0f`) e rastreamento contínuo da coordenada de toque (`touchedX`) em `float`, evitando saltos/degraus visíveis mesmo quando o `Max` lógico da barra é pequeno.

## 2. Barra de Mídia com Ondas (`WaveSeekbar`)
Classes de referência na `SystemUI.apk`:
- `com.android.systemui.media.colorfulwave.WaveSeekbarProgressDrawable` e `MultiWaveAreaTrackRenderer`.
- **Comportamento visual:**
  - Quando em reprodução, exibe faixas onduladas translúcidas sobrepostas com degradê fluido;
  - Quando pausado, recolhe suavemente para linha arredondada plana;
  - Mantém pequeno espaçamento visual após o polegar/thumb para a trilha restante.

## 3. Estética Nativa no Modo Escuro (One UI 9)
- Fundos de tela e superfícies principais permanecem em **preto / grafite neutro** (`#000000` / `#08080A` no fundo, cards escuros translúcidos limpos com borda especular sutil), mesmo com paletas de papel de parede quentes/vibrantes.
- A cor extraída do papel de parede aparece de forma contida e elegante em acentos ativos (títulos pontuais, chaves ligadas, botões primários, preenchimento de sliders) — sem tingir fortemente fundos ou cartões com tons amarronzados.
