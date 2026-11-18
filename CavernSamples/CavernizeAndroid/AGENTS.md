# Diretrizes de Desenvolvimento — CavernizeAndroid

Estas regras complementam o `AGENTS.md` da raiz quando você estiver trabalhando dentro de `CavernSamples/CavernizeAndroid/`.

## 1. Organização dos Arquivos
- **Parciais da UI (5 abas):** `MainActivity.cs` (ciclo de vida, cabeçalho compacto, navegação inferior e helpers visuais), `MainActivity.PlayerMusicTab.cs` (Player casual com capa/metadados), `MainActivity.PlayerTab.cs` (Sala 3D, controles espaciais e mixer), `MainActivity.ConverterTab.cs` (Estúdio: targets, exportação, fila em lote), `MainActivity.ExtractorTab.cs` (Stems), `MainActivity.SettingsTab.cs` (Ajustes, FFmpeg, diagnósticos, sobre/atualização).
- **Tema e Componentes Customizados (`Canvas`):** `Theme/OneUiTheme.cs` (tokens dinâmicos, vidro fosco, `OneUiBackdropView`, `OneUiSeekBar`), `Views/OneUiIconView.cs`, `Views/OneUiComponents.cs` (`OneUiInlineSelector`, `OneUiModalDialog`), `Views/OneUiWaveMediaSeekBar.cs`, `Views/RoomSimulatorView.cs`, `Views/LayoutDiagramView.cs`.
- **Áudio e Serviços:** `Audio/AndroidAudioOutput.cs`, `Audio/AndroidMediaFallbackDecoder.cs`, `Services/AppSettingsStore.cs`, `Services/AppLogger.cs`, `Services/FFmpegInstallerService.cs`, `Services/TruehddInstallerService.cs`, `Services/TrueHDDecoderService.cs`, `Services/AppUpdateService.cs`, `Services/AndroidFileHelper.cs`, `Services/ApkFileProvider.cs`, `Services/MediaMetadataHelper.cs`, `Services/MediaPlaybackNotificationManager.cs`.
- **Localização:** `Language/AndroidStrings.cs` (`pt-BR`, `en-US`, `hu-HU`, `zh-CN`). Nunca deixe texto visível hardcoded em inglês se houver versão em português; mantenha todos os idiomas consistentes ao alterar a UI.

## 2. Padrão Visual Samsung One UI 9 & Boas Práticas de UI
- **Identidade One UI 9 fiel:** evite diálogos padrão antigos do Android (`AlertDialog`, `Spinner` popup nativo) — utilize `OneUiModalDialog`, `OneUiInlineSelector`, botões em pílula com física `AttachRecoil`, ícones vetoriais próprios (`OneUiVectorIcons`, zero emojis soltos na UI) e controles inspirados na One UI 9.
- **Compatibilidade com `.NET Android` (`net11.0-android`, `SupportedOSPlatformVersion` 26.0):** o projeto não usa pacotes AndroidX externos; utilize apenas APIs nativas do namespace `Android.*` respeitando guardas de versão (`Build.VERSION.SdkInt` / `OperatingSystem.IsAndroidVersionAtLeast(...)`) e lembrando que `targetSdkVersion` no manifest deve permanecer `28`.
- **Prevenção obrigatória de corte de botões (Anti-Clipping):**
  - Todo `LinearLayout` com `Orientation.Horizontal` contendo botões ou pílulas **deve** definir `BaselineAligned = false` e `SetGravity(GravityFlags.CenterVertical)`. Sem isso, se qualquer botão quebrar em duas linhas, o Android alinha pela baseline da 1ª linha e corta metade do botão embaixo.
  - Botões em barra horizontal (`PrimaryButton`, `SecondaryButton`, `PillButton`, etc.) devem usar `SetSingleLine(true)` e `Ellipsize = TextUtils.TruncateAt.End`, ou usar `HorizontalScrollView` quando houver muitos chips.
- **Touch em controles dentro de `ScrollView`:** `RoomSimulatorView`, `OneUiSeekBar` e `OneUiWaveMediaSeekBar` implementam proteção contra interceptação acidental de rolagem vertical (`Parent?.RequestDisallowInterceptTouchEvent(...)` e limiar `touchSlop`). Preserve essa lógica ao mexer neles.
- **Diagnósticos:** registre erros e eventos importantes de arquivo/FFmpeg/mídia com `AppLogger` (gravado em `Downloads/Cavernize/Logs/cavernize_log.txt`) para facilitar depuração direta no aparelho do usuário.

## 3. Referências de Contexto Atual
- Consulte `.agents/docs/PROJECT_HISTORY.md` para ver decisões recentes (paleta neutra vs. wallpaper, cabeçalho compacto, altura 3D na sala, ordem atual de abas/cards, balões `?` explicativos).
- Consulte `.agents/docs/ONEUI9_REFERENCE.md` antes de replicar ou ajustar componentes e animações nativas da One UI 9 (inspecionadas em `SystemUI.apk`, `framework-res.apk`, `SamsungGallery2018.apk`, `SamsungVideoPlayer.apk`).
- Consulte `.agents/docs/SAVED_IDEAS.md` para ideias/animações arquivadas que o usuário gostou mas não estão ativas no momento, e atualize `README.md` quando adicionar recursos inéditos.
- Lembrete crítico: se não conseguir descompilar/encontrar um comportamento exato pedido pelo usuário em algum APK, **avise antes de intuir ou aproximar**.
