# Diretrizes Estruturais do Projeto (Cavern / CavernizeAndroid)

Este repositório é o fork pessoal (`guumachadooo/Cavern`) focado no aplicativo Android **Cavernize** (`CavernSamples/CavernizeAndroid`, pacote `hu.sbence.cavern.cavernize`), mantendo o motor espacial **Cavern** completo no celular.

## 1. Idioma e Comunicação
- **Responda sempre ao usuário em Português (Brasil)**, de forma direta e objetiva.
- Use links Markdown clicáveis com esquema `file://` ao citar qualquer arquivo ou intervalo de linhas.
- Reporte apenas arquivos, símbolos, comportamentos e resultados de builds/comandos realmente observados na sessão.

## 2. Preservação de Funcionalidades Originais
- **Mantenha 100% das funcionalidades do Cavernize original** (renderização para múltiplos targets/layouts, upmixing Matrix e Cavernize 3D, calibração HRIR e filtros QuickEQ, extração separada de canais/objetos/stems e `.json`, fila de renderização em lote, metadados de codec, relatórios pós-renderização, delay/qualidade/opções CLI) além dos recursos espaciais interativos para fone de ouvido e player Android.
- **Histórico de arquivos removidos no fork:** commits antigos antes da porta Android ainda guardam arquivos originais do Cavern/PC; pesquise no histórico Git (`git log`) se precisar recuperar alguma referência original.
- **Decodificação externa:** codecs que dependem de binários externos no Android (`FFmpeg` em `FilesDir/bin/ffmpeg` e `truehdd` oficial `aarch64-unknown-linux-musl` em `FilesDir/bin/truehdd` para Dolby TrueHD / Atmos TrueHD) devem usar os serviços gerenciados de download/execução segura em `FilesDir`, preservando limpeza de cache temporário e fallbacks seguros.

## 3. Regras Críticas ("Nunca Quebre")
1. **NUNCA altere `android:targetSdkVersion="28"`** em `CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml`. Essa configuração coloca o app no domínio SELinux `untrusted_app_27`, permitindo executar os binários estáticos (`ffmpeg` e `truehdd`) baixados pelo app dentro de `FilesDir`.
2. **Build local no Termux/Ubuntu PRoot (ARM64):** não há workload Android instalado localmente (o APK final é compilado no GitHub Actions). Ao compilar `Cavernize.Logic` ou projetos .NET localmente com `/root/.dotnet/dotnet`, use **obrigatoriamente** as variáveis de memória/globalização e flags single-proc:
   ```bash
   DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=20000000 DOTNET_EnableDiagnostics=0 /root/.dotnet/dotnet build <proj> -m:1 /p:UseSharedCompilation=false
   ```
3. **Versionamento sincronizado para Auto-Update OTA:** `CavernizeAndroid` verifica atualizações na release `android-latest` do GitHub comparando versões numéricas (`AppUpdateService.IsRemoteNewer`). Sempre mantenha `ApplicationDisplayVersion` coerente entre `CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj` e `.github/workflows/BuildAndroid.yml` (nunca regrida nem repita a versão em releases públicas).
4. **Entrega do APK:** o APK assinado final produzido pelo workflow `.github/workflows/BuildAndroid.yml` deve ser salvo em `/root/.gemini/antigravity/scratch/Cavern/Cavernize-Android.apk` e copiado para `/sdcard/Download/Cavernize-Android.apk` quando disponível no ambiente do usuário.
5. **Estilo de código:** respeite `CavernSamples/Coding style.txt` (chaves na mesma linha, PascalCase em métodos/propriedades, camelCase em variáveis, sumários XML claros). Preserve correções existentes como o fallback lossless em C# para FLAC (`FlacAudioConverter.cs`) e logging completo do FFmpeg e do `truehdd`.
6. **Planejamento prévio em etapas e auditoria final:** antes de implementar qualquer mudança, recurso novo ou correção não trivial, elabore primeiro um plano dividido por etapas, siga-o passo a passo e, ao concluir, faça uma verificação rigorosa item a item confirmando que tudo foi implementado e validado corretamente.

## 4. Memória Histórica vs. Regras Permanentes (Retroalimentação Contínua)
- Este arquivo (`AGENTS.md`) contém apenas **invariantes estruturais permanentes**.
- Para entender decisões evolutivas de design, preferências atuais de interface e lições de versões anteriores sem tratá-las como regras imutáveis caso o usuário peça mudanças futuras, leia `.agents/docs/PROJECT_HISTORY.md` e `.agents/docs/ONEUI9_REFERENCE.md` sob demanda.
- **Retroalimentação obrigatória pelos agentes:** sempre que resolver um bug não óbvio (Android, FFmpeg, CI, assinaturas `.NET`, layout), descobrir uma nova restrição técnica do ambiente ou implementar uma nova preferência/funcionalidade pedida pelo usuário:
  1. Adicione um resumo objetivo (1 a 4 linhas) em `.agents/docs/PROJECT_HISTORY.md` (consolidando entradas antigas se necessário para mantê-lo enxuto).
  2. Registre achados técnicos de componentes nativos Samsung One UI / `SystemUI.apk` em `.agents/docs/ONEUI9_REFERENCE.md`.
  3. Só edite `AGENTS.md` (raiz ou subpasta Android) se surgir uma **nova invariante permanente** ("nunca quebre") ou novo módulo estrutural do projeto — nunca transforme preferências estéticas pontuais em regras eternas.
  4. Mantenha `.agents/skills/` e `.agents/hooks/` atualizados se o pipeline de build, caminhos ou SDKs mudarem.

## 5. Mapa Rápido do Código
- `Cavern/` e `Cavern.Format/`: motor DSP espacial, formatos e decodificadores base.
- `CavernSamples/Reusable/Cavernize.Logic/`: lógica compartilhada multiplataforma (`RealtimePlayerEngine`, `StemExtractor`, `FlacAudioConverter`, `CavernizeOutput`, `MergeToContainer`, modelos e strings).
- `CavernSamples/CavernizeAndroid/`: app Android completo em `.NET` (`net11.0-android`, APIs nativas Android sem AndroidX externo). Há um `AGENTS.md` específico dentro desta pasta carregado automaticamente quando você editar o app.
- `.github/workflows/BuildAndroid.yml`: pipeline oficial de build, assinatura com keystore persistente e publicação da release `android-latest`.
- `SystemUI.apk` e logs do dispositivo: para depuração segura e leitura de logs em `/sdcard/Download/Cavernize/Logs/cavernize_log.txt`, siga a skill `.agents/skills/cavernize-device-debug/SKILL.md` (sempre respeitando limites de acesso/SELinux, sem tentar bypass).
