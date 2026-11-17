using System.Globalization;

using VoidX.WPF.Language;

namespace CavernizeAndroid.Language;

/// <summary>
/// Full UI localization for CavernizeAndroid (pt-BR, en-US, hu-HU, zh-CN).
/// </summary>
public sealed class AndroidStrings {
    static AndroidStrings active;
    readonly Dictionary<string, string> strings;

    public static AndroidStrings Active => active ??= Load();

    public static void Reload() {
        active = null;
        _ = Active;
    }

    public string this[string key] => strings.TryGetValue(key, out string val) ? val : key;

    AndroidStrings(Dictionary<string, string> strings) => this.strings = strings;

    static AndroidStrings Load() {
        string culture = !string.IsNullOrEmpty(LanguageSettings.CultureOverride)
            ? LanguageSettings.CultureOverride
            : CultureInfo.CurrentUICulture.Name;

        Dictionary<string, string> dict = CreateEnglish();
        string lang = culture.Split('-')[0].ToLowerInvariant();
        if (lang == "pt") {
            ApplyPortuguese(dict);
        } else if (lang == "hu") {
            ApplyHungarian(dict);
        } else if (lang == "zh") {
            ApplyChinese(dict);
        }
        return new AndroidStrings(dict);
    }

    static Dictionary<string, string> CreateEnglish() => new() {
        ["AppTitle"] = "Cavernize",
        ["OpenFile"] = "Open File",
        ["NoFile"] = "No file loaded (supports EAC3, AC3, MKV, MP4, WAV/ADM, CAF, LAF, ATMOS, MP3/FLAC)",
        ["TrackLabel"] = "Track:",
        ["TabPlayer"] = "3D Player",
        ["TabConvert"] = "Convert",
        ["TabExtract"] = "Extractor",
        ["TabSettings"] = "Queue & Setup",

        // Player & Room
        ["RoomTitle"] = "3D Room Simulator (drag listener to move)",
        ["PresetCenter"] = "Center",
        ["PresetFront"] = "Front",
        ["PresetBack"] = "Back",
        ["PresetLeft"] = "Left",
        ["PresetRight"] = "Right",
        ["PresetCeiling"] = "High",
        ["PosX"] = "Left / Right (X)",
        ["PosZ"] = "Back / Front (Z)",
        ["PosY"] = "Height (Y)",
        ["RotYaw"] = "Head Turn (Yaw)",
        ["RotPitch"] = "Head Tilt (Pitch)",
        ["HeadphoneVirt"] = "Headphone 3D Virtualizer (HRTF)",
        ["DistSim"] = "Ear Distance Simulation (HRTF Distancer)",
        ["RolloffLabel"] = "Distance Rolloff:",
        ["MasterVol"] = "Volume",
        ["LfeVol"] = "Subwoofer (LFE)",
        ["Play"] = "Play",
        ["Pause"] = "Pause",
        ["Stop"] = "Stop",
        ["Loop"] = "Loop",
        ["FilterAll"] = "All",
        ["Filter3DOnly"] = "3D Objects Only",
        ["FilterHeightOnly"] = "Height / Ceiling Only",
        ["FilterBedsOnly"] = "Beds Only",
        ["MixerTitle"] = "Live Channels & 3D Objects (Solo / Mute)",
        ["ClearSoloMute"] = "Reset Solo/Mute",

        // Converter
        ["SystemTitle"] = "System & Output Target",
        ["RenderTarget"] = "Render target:",
        ["SelectTarget"] = "Choose Layout...",
        ["DisplayWiring"] = "Display Wiring",
        ["CatPC"] = "PC / Standard (<= 8 ch)",
        ["CatMatrix"] = "Matrix Wired",
        ["CatMulti"] = "Multichannel (> 8 ch)",
        ["ExportFormat"] = "Output codec:",
        ["Container"] = "Container / Format:",
        ["ApplyRoomToExport"] = "Apply Player 3D room position & Solo/Mute to export",
        ["RenderBtn"] = "Render & Export",
        ["AddQueueBtn"] = "Add to Queue",
        ["ShowMetaBtn"] = "Codec Metadata",
        ["ShowReportBtn"] = "Post-Render Report",
        ["Ready"] = "Ready",
        ["Starting"] = "Starting render...",
        ["ExportDone"] = "Finished! Saved to: {0}",
        ["ReportDone"] = "Analysis complete! Check the Post-Render Report.",
        ["ShareFile"] = "Share",
        ["OpenPlayer"] = "Open in Player",

        // Extractor
        ["ExtractTitle"] = "Channel & 3D Object Stem Extractor",
        ["ExtractDesc"] = "Separate every bed channel and every 3D object into individual WAV files (works with Dolby Digital Plus Atmos E-AC-3 JOC, ADM BWF, DAMF, LAF, WAV, CAF).",
        ["ExtractMode"] = "Extraction mode:",
        ["ModeRawStems"] = "Extract All Original Separated (Mono WAV per Channel & Object + 3D JSON)",
        ["ModeSpatialEach"] = "Render Each Selected Item in 3D Separately (HRTF / Target Layout)",
        ["Mode3DCombined"] = "Render Only Selected 3D Objects Combined (1 Spatial File)",
        ["ModeRawBitstream"] = "Extract Raw Container Bitstream (--extract)",
        ["BitDepthLabel"] = "Bit depth:",
        ["ExportJsonCheck"] = "Export 3D object trajectory metadata (.json)",
        ["SelectAll"] = "All",
        ["Select3D"] = "3D Objects",
        ["SelectBeds"] = "Beds",
        ["InvertSel"] = "Invert",
        ["ExtractBtn"] = "Extract to Downloads/Cavernize",
        ["CancelBtn"] = "Cancel",
        ["ExtractDone"] = "Extracted {0} file(s) to {1}",

        // Queue & Settings
        ["QueueTitle"] = "Render Queue",
        ["BatchAddBtn"] = "Batch Add Files",
        ["RemoveQueueBtn"] = "Remove",
        ["ProcessQueueBtn"] = "Process Queue",
        ["UpmixTitle"] = "Upmixing Setup",
        ["MatrixUpmix"] = "Matrix Upmixing (Fill 7.1 bed)",
        ["CavernizeUpmix"] = "Cavernize 3D (Generate height objects)",
        ["UpmixEffect"] = "Upmixing Effect (%)",
        ["UpmixSmooth"] = "Upmixing Smoothness (%)",
        ["ResetUpmix"] = "Reset Upmixing",
        ["RenderingTitle"] = "Rendering Settings & Filters",
        ["LoadHRIR"] = "Load Custom HRIR (.wav)",
        ["ResetHRIR"] = "Reset Default HRIR",
        ["SpeakerVirt"] = "Height virtualization on speakers",
        ["LoadFilters"] = "Load QuickEQ Room Filters (.txt)",
        ["ClearFilters"] = "Clear Filters",
        ["MuteBed"] = "Mute bed channels (3D objects only)",
        ["MuteGround"] = "Mute ground channels (height only)",
        ["Force24Bit"] = "Force 24-bit PCM",
        ["SurroundSwap"] = "Swap side / rear channels",
        ["WavSkip"] = "Skip RIFF WAVE channel mask",
        ["ReportMode"] = "Report only mode (no disk output)",
        ["QualityGrading"] = "Quality analysis & grading",
        ["AdvancedTitle"] = "Advanced & CLI Options",
        ["RenderGain"] = "Render Gain (dB)",
        ["HeightSqueeze"] = "Height Squeeze (%)",
        ["MlpPres"] = "MLP Presentation (-1 = auto)",
        ["EnvType"] = "Environment Type:",
        ["RoomSize"] = "Room Size (X, Y, Z)",
        ["AudioQuality"] = "Audio Quality:",
        ["Pedantic"] = "Pedantic standard checks (--pedantic)",
        ["Unsafe"] = "Bypass standard checks (--unsafe)",
        ["KeepTemp"] = "Keep temporary files",
        ["NormalizerCheck"] = "Dynamic Normalizer / Limiter",
        ["LimiterOnlyCheck"] = "Limiter only (never boost > 0 dB)",
        ["FFmpegBtn"] = "Locate FFmpeg Executable",
        ["FFmpegArgsLabel"] = "Extra FFmpeg args (e.g. -b:a 768k):",
        ["LanguageTitle"] = "Language & Help",
        ["LanguageLabel"] = "App Language:",
        ["UserGuideBtn"] = "Online User Guide",
        ["AboutBtn"] = "About Cavernize",
        ["StoragePermBtn"] = "Grant Full File Access (for large MKV / .atmos)",

        // Wiring & Messages
        ["WrGui"] = "Wiring Guide",
        ["ChCon"] = "Connect the {0} speaker to your system's {1} output.",
        ["ChCMx"] = "Connect the {0} speaker's + terminal to {1}'s + and - terminal to {2}'s +.",
        ["Over8"] = "There is no standard wiring over 8 channels. Order in file:{0}",
        ["ErrorTitle"] = "Error",
        ["NoTrack"] = "Please load a file with a supported track first.",
        ["OpRunning"] = "An operation is already running.",
        ["FFmpegNeeded"] = "The selected output codec ({0}) requires FFmpeg. Configure FFmpeg in Setup or choose PCM / ADM BWF / DAMF / LAF.",
        ["TrueHDUnsupported"] = "TrueHD MLP decoding requires truehdd. Convert to E-AC-3, ADM BWF, or DAMF on PC, or locate truehdd if supported.",
        ["CopyingMedia"] = "Preparing media file...",
        ["DecodingFallback"] = "Decoding audio via Android MediaCodec...",
    };

    static void ApplyPortuguese(Dictionary<string, string> d) {
        d["OpenFile"] = "Abrir Arquivo";
        d["NoFile"] = "Nenhum arquivo aberto (EAC3, AC3, MKV, MP4, WAV/ADM, CAF, LAF, ATMOS, MP3/FLAC)";
        d["TrackLabel"] = "Faixa:";
        d["TabPlayer"] = "Player 3D";
        d["TabConvert"] = "Converter";
        d["TabExtract"] = "Extrator";
        d["TabSettings"] = "Fila & Config";

        d["RoomTitle"] = "Simulador de Sala 3D (arraste o ouvinte pela sala)";
        d["PresetCenter"] = "Centro";
        d["PresetFront"] = "Frente";
        d["PresetBack"] = "Fundo";
        d["PresetLeft"] = "Esquerda";
        d["PresetRight"] = "Direita";
        d["PresetCeiling"] = "Alto";
        d["PosX"] = "Esquerda / Direita (X)";
        d["PosZ"] = "Fundo / Frente (Z)";
        d["PosY"] = "Altura (Y)";
        d["RotYaw"] = "Giro da Cabeça (Yaw)";
        d["RotPitch"] = "Inclinação (Pitch)";
        d["HeadphoneVirt"] = "Virtualizador 3D para Fones (HRTF)";
        d["DistSim"] = "Simulação de Distância Acústica (HRTF Distancer)";
        d["RolloffLabel"] = "Atenuação por Distância:";
        d["MasterVol"] = "Volume";
        d["LfeVol"] = "Subwoofer (LFE)";
        d["Play"] = "Tocar";
        d["Pause"] = "Pausar";
        d["Stop"] = "Parar";
        d["Loop"] = "Repetir";
        d["FilterAll"] = "Tudo";
        d["Filter3DOnly"] = "Só Objetos 3D";
        d["FilterHeightOnly"] = "Só Teto / Altura";
        d["FilterBedsOnly"] = "Só Canais Base";
        d["MixerTitle"] = "Canais e Objetos 3D ao Vivo (Solo / Mute)";
        d["ClearSoloMute"] = "Limpar Solo/Mute";

        d["SystemTitle"] = "Sistema e Alvo de Renderização";
        d["RenderTarget"] = "Alvo de saída:";
        d["SelectTarget"] = "Escolher Layout...";
        d["DisplayWiring"] = "Mostrar Cabeamento";
        d["CatPC"] = "PC / Padrão (<= 8 canais)";
        d["CatMatrix"] = "Matrizado (Matrix)";
        d["CatMulti"] = "Multicanal (> 8 canais)";
        d["ExportFormat"] = "Codec de saída:";
        d["Container"] = "Contêiner / Arquivo:";
        d["ApplyRoomToExport"] = "Aplicar posição da sala e Solo/Mute do Player na exportação";
        d["RenderBtn"] = "Renderizar e Salvar";
        d["AddQueueBtn"] = "Adicionar à Fila";
        d["ShowMetaBtn"] = "Metadados do Codec";
        d["ShowReportBtn"] = "Relatório Pós-Render";
        d["Ready"] = "Pronto";
        d["Starting"] = "Iniciando renderização...";
        d["ExportDone"] = "Concluído! Salvo em: {0}";
        d["ReportDone"] = "Análise concluída! Veja o Relatório Pós-Render.";
        d["ShareFile"] = "Compartilhar";
        d["OpenPlayer"] = "Ouvir no Player";

        d["ExtractTitle"] = "Extrator Separado de Canais e Objetos 3D";
        d["ExtractDesc"] = "Separe cada canal fixo e cada objeto 3D em arquivos WAV individuais (funciona inclusive em Dolby Digital Plus Atmos comprimido E-AC-3 JOC, ADM BWF, DAMF, LAF, WAV, CAF).";
        d["ExtractMode"] = "Modo de extração:";
        d["ModeRawStems"] = "Extrair Tudo Separado Original (1 WAV Mono por Canal e Objeto + JSON 3D)";
        d["ModeSpatialEach"] = "Renderizar Cada Item Selecionado em 3D Separado (HRTF / Layout)";
        d["Mode3DCombined"] = "Renderizar Apenas Objetos 3D Combinados (1 Arquivo Espacial)";
        d["ModeRawBitstream"] = "Extrair Bitstream Bruto do Contêiner (--extract)";
        d["BitDepthLabel"] = "Resolução (bits):";
        d["ExportJsonCheck"] = "Exportar trajetórias 3D dos objetos (.json)";
        d["SelectAll"] = "Todos";
        d["Select3D"] = "Objetos 3D";
        d["SelectBeds"] = "Canais Base";
        d["InvertSel"] = "Inverter";
        d["ExtractBtn"] = "Extrair para Downloads/Cavernize";
        d["CancelBtn"] = "Cancelar";
        d["ExtractDone"] = "{0} arquivo(s) extraído(s) em {1}";

        d["QueueTitle"] = "Fila de Renderização";
        d["BatchAddBtn"] = "Adicionar Lote";
        d["RemoveQueueBtn"] = "Remover";
        d["ProcessQueueBtn"] = "Processar Fila";
        d["UpmixTitle"] = "Configuração de Upmixing";
        d["MatrixUpmix"] = "Matrix Upmixing (Preencher 7.1)";
        d["CavernizeUpmix"] = "Cavernize 3D (Gerar objetos de altura)";
        d["UpmixEffect"] = "Intensidade do Efeito (%)";
        d["UpmixSmooth"] = "Suavidade (%)";
        d["ResetUpmix"] = "Resetar Upmixing";
        d["RenderingTitle"] = "Opções de Renderização e Filtros";
        d["LoadHRIR"] = "Carregar HRIR Personalizado (.wav)";
        d["ResetHRIR"] = "Restaurar HRIR Padrão";
        d["SpeakerVirt"] = "Virtualização de altura em caixas";
        d["LoadFilters"] = "Carregar Filtros QuickEQ (.txt)";
        d["ClearFilters"] = "Limpar Filtros";
        d["MuteBed"] = "Silenciar canais base (Só objetos 3D)";
        d["MuteGround"] = "Silenciar chão (Só altura/teto)";
        d["Force24Bit"] = "Forçar PCM 24-bit";
        d["SurroundSwap"] = "Trocar canais Side / Rear";
        d["WavSkip"] = "Ignorar máscara de canais RIFF WAVE";
        d["ReportMode"] = "Modo apenas relatório (não salva arquivo)";
        d["QualityGrading"] = "Análise de qualidade e notas";
        d["AdvancedTitle"] = "Configurações Avançadas e CLI";
        d["RenderGain"] = "Ganho de Renderização (dB)";
        d["HeightSqueeze"] = "Compressão de Altura - Height Squeeze (%)";
        d["MlpPres"] = "Apresentação MLP (-1 = auto)";
        d["EnvType"] = "Tipo de Ambiente:";
        d["RoomSize"] = "Tamanho da Sala (X, Y, Z)";
        d["AudioQuality"] = "Qualidade do Áudio:";
        d["Pedantic"] = "Verificações estritas (--pedantic)";
        d["Unsafe"] = "Ignorar travas de padrão (--unsafe)";
        d["KeepTemp"] = "Manter arquivos temporários";
        d["NormalizerCheck"] = "Normalizador / Limitador Dinâmico";
        d["LimiterOnlyCheck"] = "Apenas limitador (não sobe acima de 0 dB)";
        d["FFmpegBtn"] = "Localizar Executável FFmpeg";
        d["FFmpegArgsLabel"] = "Argumentos extras do FFmpeg (ex: -b:a 768k):";
        d["LanguageTitle"] = "Idioma e Ajuda";
        d["LanguageLabel"] = "Idioma do Aplicativo:";
        d["UserGuideBtn"] = "Guia do Usuário Online";
        d["AboutBtn"] = "Sobre o Cavernize";
        d["StoragePermBtn"] = "Permitir Acesso Total a Arquivos (MKV / .atmos)";

        d["WrGui"] = "Guia de Cabeamento";
        d["ChCon"] = "Conecte a caixa {0} na saída {1} do seu sistema.";
        d["ChCMx"] = "Conecte o terminal + da caixa {0} no + de {1} e o terminal - no + de {2}.";
        d["Over8"] = "Não há cabeamento padrão acima de 8 canais. Ordem exata no arquivo:{0}";
        d["ErrorTitle"] = "Erro";
        d["NoTrack"] = "Abra primeiro um arquivo com uma faixa suportada.";
        d["OpRunning"] = "Já existe uma operação em andamento.";
        d["FFmpegNeeded"] = "O codec escolhido ({0}) precisa do FFmpeg. Configure o FFmpeg em Configurações ou escolha PCM / ADM BWF / DAMF / LAF.";
        d["TrueHDUnsupported"] = "A decodificação TrueHD MLP requer o truehdd. Converta no PC ou selecione outra faixa (ex: E-AC-3 JOC).";
        d["CopyingMedia"] = "Preparando arquivo de mídia...";
        d["DecodingFallback"] = "Decodificando áudio via MediaCodec do Android...";
    }

    static void ApplyHungarian(Dictionary<string, string> d) {
        d["OpenFile"] = "Fájl megnyitása";
        d["TrackLabel"] = "Sáv:";
        d["TabPlayer"] = "3D Lejátszó";
        d["TabConvert"] = "Konvertálás";
        d["TabExtract"] = "Kivonó";
        d["TabSettings"] = "Sor & Beáll.";
        d["Play"] = "Lejátszás";
        d["Pause"] = "Szünet";
        d["Stop"] = "Leállítás";
        d["RenderBtn"] = "Renderelés";
        d["AddQueueBtn"] = "Sorhoz adás";
        d["ShowMetaBtn"] = "Kodek metaadatok";
        d["ShowReportBtn"] = "Renderelési jelentés";
        d["WrGui"] = "Bekötési útmutató";
        d["ErrorTitle"] = "Hiba";
    }

    static void ApplyChinese(Dictionary<string, string> d) {
        d["OpenFile"] = "打开文件";
        d["TrackLabel"] = "音轨:";
        d["TabPlayer"] = "3D 播放器";
        d["TabConvert"] = "转换导出";
        d["TabExtract"] = "分轨提取";
        d["TabSettings"] = "队列与设置";
        d["Play"] = "播放";
        d["Pause"] = "暂停";
        d["Stop"] = "停止";
        d["RenderBtn"] = "开始渲染";
        d["AddQueueBtn"] = "添加到队列";
        d["ShowMetaBtn"] = "编码器元数据";
        d["ShowReportBtn"] = "渲染后报告";
        d["WrGui"] = "接线指南";
        d["ErrorTitle"] = "错误";
    }
}
