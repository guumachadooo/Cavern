# Histórico e Contexto Vivo do Projeto (`PROJECT_HISTORY.md`)

> **Nota para agentes futuros:** Este arquivo documenta a história, lições aprendidas e decisões até o ciclo atual (**v2.5.x**). Use-o para entender *por que* o código está como está hoje e evitar regressões — **não** trate preferências pontuais de layout ou estilo aqui descritas como restrições eternas se o usuário pedir mudanças futuras.
> **Retroalimentação:** Ao finalizar novos marcos, correções não óbvias ou mudanças de UX relevantes, atualize este arquivo de forma concisa (consolidando texto antigo se ficar longo).

## Ambiente do Usuário
- **Dispositivo principal de teste e uso:** Samsung Galaxy S25 Ultra rodando Android 17 / One UI 9 (`SystemUI.apk` do aparelho foi usada para consultar o comportamento real de sliders e media player da One UI 9).
- **Ambiente do agente:** container Ubuntu rodando dentro do Termux no próprio celular do usuário, com acesso ao repositório em `/root/.gemini/antigravity/scratch/Cavern` e entrega direta do APK em `/sdcard/Download/Cavernize-Android.apk` após build no GitHub Actions.
- **Compilação:** como não há Android SDK / workload Android instalado dentro do PRoot ARM64 local, toda geração do `.apk` assinado passa por push no `master` + workflow `.github/workflows/BuildAndroid.yml`. Localmente valida-se código compartilhado com `/root/.dotnet/dotnet` (`.NET 8`) usando flags de limite de heap GC.

## Evolução por Versões e Decisões Importantes

### Fase 1 — Porta Android Funcional (`v2.0` -> `v2.1`)
- Criação do projeto `CavernizeAndroid` e separação da lógica compartilhada em `CavernSamples/Reusable/Cavernize.Logic/`.
- Adição do player espacial em tempo real (`RealtimePlayerEngine` + `AndroidAudioOutput`), simulador de sala 3D interativo com fones de ouvido (HRTF), extrator de canais/objetos 3D (`StemExtractor`), fallback de decodificação Android (`AndroidMediaFallbackDecoder`) para formatos não nativos, conversor lossless puro em C# para `.flac` (`FlacAudioConverter`) e correções de extensão no `MergeToContainer`.
- `FFmpeg` estático ARM64 baixado com 1 clique pelo usuário (`FFmpegInstallerService`) dentro de `FilesDir/bin/ffmpeg` — motivo pelo qual `targetSdkVersion="28"` precisa ser preservado no `AndroidManifest.xml`.

### Fase 2 — Redesign One UI 9, Monet & Logging (`v2.2.x`)
- Implementação do `OneUiTheme` com suporte a cores dinâmicas de papel de parede (Material You / API 31+), fundo desfocado (`OneUiBackdropView`), ícones vetoriais próprios (`OneUiIconView`), seletores in-app (`OneUiInlineSelector`), diálogos modais (`OneUiModalDialog`), `OneUiWaveMediaSeekBar` (ondas animadas inspiradas no player da tela de bloqueio/notificação da One UI 9), `AppUpdateService` (OTA via tag `android-latest` no GitHub) e `AppLogger` compartilhado (`Downloads/Cavernize/Logs/`).
- **Bugs corrigidos nessa fase (não regredir):**
  - Bug no cálculo de versão do `BuildAndroid.yml` que acusava mesma versão no auto-updater (corrigido alinhando `ApplicationDisplayVersion` no workflow com a versão crescente real).
  - Erro de FFmpeg sem detalhes no diagnóstico: `FFmpeg.cs` passou a capturar `stdout`/`stderr` completos e gravar no `AppLogger`.
  - Diálogos antigos nativos e spinners do Android substituídos por componentes One UI 9 in-app.

### Fase 3 — Polimento UX & Recursos da `v2.3.x` (Estado Atual)
Feedbacks diretos do usuário após testar no S25 Ultra e soluções aplicadas:
1. **Paleta Escura Mais Neutra vs. Papel de Parede:** quando o papel de parede era laranja, misturar `system_neutral` nos fundos/cards deixava tudo marrom/alaranjado em excesso. A preferência atual foi manter fundos e cards no escuro neutro característico da One UI 9 nativa (como Settings / Voice Recorder), deixando a cor do papel de parede agir com mais suavidade principalmente nos acentos (switches, pílulas ativas, sliders).
2. **Cabeçalho Minimalista e Expansível:** o card superior grande ocupava muito espaço e mostrava 4 linhas técnicas o tempo todo; foi substituído por uma barra superior enxuta com pílula de arquivo clicável que expande detalhes técnicos e seletor múltiplo de faixas apenas sob demanda.
3. **Botões Cortados:** `Adicionar Lote` (e outras pílulas em linhas horizontais) tinham a metade inferior cortada porque `LinearLayout` horizontal tem `BaselineAligned = true` por padrão e botões de 2 linhas quebravam a altura. Corrigido com `BaselineAligned = false` e `SetSingleLine(true)`.
4. **Sala 3D com Visualização Vertical (Altura Y) e Gimbal Desacoplado:**
   - O gimbal 3D no canto superior direito cobria caixas como `FR`/`TFR`; foi movido para um dock dedicado abaixo da sala.
   - O gráfico puramente 2D de topo não mostrava claramente movimento vertical (`Y` / altura / teto); foram adicionados modos de visualização 3D/elevação vertical (`Y`) + controle dedicado de altura para enxergar caixas e objetos subindo/descendo.
5. **Notificação de Mídia no Sistema (estilo Spotify):** integração com `MediaSession` + `Notification.MediaStyle` (`POST_NOTIFICATIONS` + `FOREGROUND_SERVICE`) para controlar Play/Pause, `-10s`, `+10s`, Stop e barra de progresso pela central de notificações e tela de bloqueio.
6. **Sliders Contínuos com Bolinha Animada (`OneUiSeekBar`):** remoção do "degrau" discreto ao arrastar valores pequenos (passou a usar razão contínua `float` pixel a pixel com suavização) e adição da bolinha circular animada ao tocar/arrastar, inspirada na `SystemUI.apk` da One UI 9.
7. **Balões `?` de Ajuda e Reorganização por Uso:** inclusão de explicações curtas e acessíveis via botão `?` nos cards + movimentação da `Fila de Renderização` para a aba `Estúdio` + reordenação da aba `Ajustes` dos ajustes de áudio mais frequentes no topo até setup único (FFmpeg, Logs, Sobre/Updates) no rodapé.
8. **Consistência de Idioma:** cuidar para que filtros, headers, notificações e textos novos tenham tradução limpa em português (`AndroidStrings.cs`) sem vazar frases soltas em inglês quando em `pt-BR`.

### Fase 4 — Aba Casual `Player`, Capa Embutida & Sala 3D Orbitável (`v2.4.x` — commits `ce62a8e1`, `46d17a61`)
1. **Aba `Player` dedicada (`MainActivity.PlayerMusicTab.cs` + `MediaMetadataHelper.cs`):** extrai capa embutida (ou capa de pasta / capa procedural) e tags de mídia (`MediaMetadataRetriever`) com destaque visual estilo player musical, barra inferior fosca e adaptação cromática da capa; mantém a aba técnica `Sala 3D` (`MainActivity.PlayerTab.cs`) e os fluxos originais completos em paralelo (totalizando 5 abas).
2. **Sala 3D Orbitável (`RoomSimulatorView.cs`):** evolução da sala 3D interativa em 60fps para inspeção espacial e de altura.

### Fase 5 — Suporte Completo a Dolby TrueHD / Dolby Atmos TrueHD (`v2.5.x`)
1. **Motor externo oficial `truehdd` (`TruehddInstallerService.cs` + `TrueHDDecoderService.cs` + `Truehdd.cs` multiplataforma):** download automático do binário estático `aarch64-unknown-linux-musl` (`x86_64` para emuladores) com licença Apache-2.0 e execução segura em `FilesDir/bin/truehdd` (mesmo domínio SELinux `untrusted_app_27` do FFmpeg).
2. **Integração total nas 5 abas e cache limpo:** decodifica faixas TrueHD/Atmos para DAMF/CAF com progresso e cancelamento generacional imediato (habilitando reprodução no `Player`/`Sala 3D`, conversão/`Queue` no `Estúdio`, extração de Stems/`.json`/`--extract` em `Stems` e troca dinâmica de `Apresentação MLP`), limpando caches temporários automaticamente (salvo `KeepTempFiles`).
3. **Fallback seguro de contêineres e Matroska/DAMF (`v2.5.16`):** corrigidos `ChannelPrototype.FromStandardName`, `DolbyAtmosMasterFormatRootFile`/`MetadataFile`, loop infinito em `MatroskaTree.GetChild(reader, tag, index)` (onde `nextTag` não avançava ao encontrar `Cues`/`Tags` no fim do MKV após `100%`) e `MatroskaReader.Seek(-1)`.

### Fase 6 — Fidelidade Exata One UI 9, Vídeo Player + Letras Apple Music (`v2.6.x`)
1. **Descoberta exata de cores & sliders (`framework-res.apk` + `SystemUI.apk`):** `quickpanel_progress` (`UID="4-8-5"`) usa exatamente `system_accent2_300` (e não `system_accent3_300`); `OneUiSeekBar` passou a ser 100% sólido sem bolinha em repouso (`!isTouchDown`) e a exibir a bolinha com fade `0.50f` apenas durante o arrasto, além de abandonar a deformação côncava (arquivada em `SAVED_IDEAS.md`).
2. **Barra inferior estilo Samsung Gallery (`SamsungGallery2018.apk`), ícones harmônicos e Sala 3D refinada:** navegação inferior flutuante com ícones line/solid semitransparentes + pílula `#1AFFFFFF`, badges harmônicos ligados à paleta do sistema, zoom in/out por pinça e botões na Sala 3D, slider de altura no tema correto e transporte simétrico (`Reiniciar`, `-10s`, `Play/Pause`, `+10s`, `Repetir`).
3. **Player de Vídeo Sincronizado + Letras Sincronizadas (`.lrc` e embutidas):** suporte a vídeo mudo sincronizado (`TextureView` + `MediaPlayer`) com gestos estilo YouTube (`±10s` no duplo toque, overlay e velocidade) e interface/letras em tela cheia inspiradas no Apple Music.
4. **Histórico preservado no GitHub Releases & `README.md` atualizado:** restauradas todas as versões anteriores no GitHub Releases e configurado o workflow para nunca sobrescrever releases versionadas anteriores.

## Lições Técnicas & Armadilhas Conhecidas (Retroalimentação)
- **`MatroskaTree.GetChild(reader, tag, index)`:** sempre avançar `nextTag = reader.Position` dentro do loop de leitura dos filhos e proteger `TryCreate` contra `reader.Position >= endPosition`, caso contrário tags finais após os `Cluster`s (como `Cues`/`Tags`) causam loop infinito em `100%` durante a extração.
- **`.NET Android` (`net11.0-android`):** `View` expõe `ScaleX`/`ScaleY` como propriedades em C# (não métodos `SetScaleX`/`SetScaleY`); `MediaMetadataRetriever` pode exigir cast numérico de metadado `(MetadataKey)38` (`METADATA_KEY_SAMPLERATE`) dependendo do nível de binding; `AppLogger.Error` aceita `(string context, Exception ex)` ou `(string message)` (2 ou 1 argumento); `ReadableMetadataHeader` exige `IReadOnlyList<ReadableMetadataField>`; arquivos com `using Android.OS;` devem qualificar `System.OperationCanceledException` (colide com `Android.OS.OperationCanceledException`).
- **Compatibilidade `Cavern.Format` (`netstandard2.1`):** usa C# 8.0 (não usar `is not` nem recursos de C# 9+).
- **`truehdd` releases:** ativos Linux musl usam `.tar.gz` (e existe `.tar.gz.sha256` junto, portanto filtrar exatamente pelo sufixo sem `.sha256` e extrair via `GZipStream` + `TarFile`); extrair faixas via `ExtractTrackFromContainer` exige `source.Track.Source.Seek(0)` antes da leitura.
- **Versionamento OTA:** o workflow `BuildAndroid.yml` sobrepõe `-p:ApplicationDisplayVersion=2.5.${{ github.run_number }}` — manter `CavernizeAndroid.csproj` e `BuildAndroid.yml` sincronizados.
- **Testes no aparelho:** o agente analisa erros pelo log em `/sdcard/Download/Cavernize/Logs/cavernize_log.txt` e pode consultar `SystemUI.apk` em modo leitura; não tentar instalar silenciosamente nem contornar permissões/SELinux.

*(Agentes: mantenham esta seção atualizada de forma enxuta quando novos ciclos de versão ou lições relevantes surgirem.)*
