# Cavernize Spatial Audio (Android Edition)

Portabilidade nativa para **Android (Kotlin + Jetpack Compose)** do ecossistema **Cavern / CavernizeGUI / Enhanced AC-3 Merger**, oferecendo decodificação, renderização espacial 3D baseada em objetos, virtualização binaural para fones, upmixing, edição de filtros/EQ/convolução e fusão de bitstreams E-AC3.

## Recursos Portados

- **Motor Cavern Core 3D**:
  - Suporte completo aos **29 canais de referência (`ReferenceChannel`)** e **31 alvos de renderização (`RenderTarget`)** (de `2.0` até `9.1.6`, alvos matriciais com inversão de fase, `Driver default` e `Headphone Virtualizer` HRTF binaural).
  - Visualizador interativo em 3D (`StandardLayoutDisplayCanvas`) com cubo espacial de caixas de solo e altura, objetos Atmos dinâmicos em tempo real, medidores de pico (VU) e **Diagrama de Cabeamento (`Display Wiring`)**.
- **Formatos e Containers (`Cavern.Format`)**:
  - Leitura e inspeção de containers e bitstreams: `RIFF WAVE / RF64`, `ADM Broadcast Wave Format (ITU-R BS.2076 axml + chna)`, `Limitless Audio Format (.laf)`, `Core Audio Format (.caf)`, `Enhanced AC-3 / AC-3 com Joint Object Coding (Dolby Atmos JOC)`, `Dolby TrueHD / Meridian Lossless Packing (.mlp)`, `Matroska (.mkv/.mka/.webm)`, `ISO Base Media (.mp4/.mov)`, `SMPTE MXF (.mxf)` e `Dolby Atmos Master Format (.atmos)`.
  - Exportação binária com suporte a `WAVEFORMATEXTENSIBLE`, chunks `ADM BWF`, `.laf` e `.eac3`, além de **Gerador de Demonstração 3D Integrado** e prévia binaural ao vivo via `AudioTrack`.
- **Laboratório DSP, Upmixer & QuickEQ**:
  - Configuração de **Upmixing Matricial & Cavernize 3D (Altura)**.
  - **Editor de Filtro Biquad & Fase** (`PK`, `LP`, `HP`, `BP`, `NO`, `AP`, `LS`, `HS`) com resposta em frequência e fase em tempo real.
  - **Equalizador Paramétrico Multibanda (`EQEditor`)** com exportação para **Equalizer APO (`.txt`)**.
  - **Editor de Convolução & Impulso HRIR (`ConvolutionEditor`)** com análise de polaridade, fase média e atraso de grupo.
- **Enhanced AC-3 Merger**:
  - Ferramenta integrada para vincular canais de Cama Obrigatória (`2.0` a `5.1`), Solo Opcional (`FLC`, `FRC`, `RL`, `RR`, `WL`, `WR`) e Altura Opcional (`TFL`, `TFR`, `TSL`, `TSR`) em um único fluxo `.eac3`.
- **Relatório Pós-Renderização & Inspetor de Metadados**:
  - Auditoria espacial de camas reais, objetos dinâmicos e notas de qualidade (`A+` a `F`) para Macrodinâmica, Microdinâmica, Impacto LFE (*Chest Slam*), Uso Surround e Altura.
- **Interface Bilíngue**:
  - Alternância instantânea entre **Português Brasileiro (PT-BR)** e **Inglês (EN)**.
