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
        ["NoFile"] = "No file loaded (supports TrueHD Atmos, EAC3, AC3, MKV, MP4, WAV/ADM, CAF, LAF, ATMOS, MP3/FLAC)",
        ["TrackLabel"] = "Track:",
        ["TabMusicPlayer"] = "Player",
        ["TabPlayer"] = "3D Room",
        ["TabConvert"] = "Studio",
        ["TabExtract"] = "Stems",
        ["TabSettings"] = "Settings",

        // Casual Music Player
        ["PlayerEmptyTitle"] = "Ready to Listen",
        ["PlayerEmptySub"] = "Open TrueHD Atmos, E-AC-3 Atmos, MKV, MP4, FLAC, or WAV",
        ["PlayerOpenRoom"] = "Customize in 3D Room",
        ["PlayerSwitchTrack"] = "Audio Track",
        ["PlayerBadgeSpatial"] = "Spatial 3D",
        ["PlayerQuickMatrix"] = "Matrix 7.1",
        ["ModeCover"] = "Cover",
        ["ModeVideo"] = "Video",
        ["ModeLyrics"] = "Lyrics",
        ["NoLyricsFound"] = "No lyrics found.\nPlace an adjacent .lrc or .txt file with the same name as this track, or open media with embedded lyrics.",
        ["LyricsTapHint"] = "Synced Live Lyrics • Tap a line to jump",
        ["LyricsUnsyncedHint"] = "Static Lyrics (.txt / tags)",
        ["VideoSyncBadge"] = "Synced Video + Spatial 3D • Double-tap ±10s",
        ["PlayerMoreTitle"] = "Playback & Spatial 3D",
        ["VideoSubtitlesTitle"] = "Subtitles (CC)",
        ["SubtitlesOff"] = "Subtitles Off",
        ["SubtitlesImportBtn"] = "Load External .srt / .vtt / .ass...",
        ["SubtitlesNoneFound"] = "No embedded or adjacent subtitle files found.",
        ["SubtitlesEmbeddedTag"] = "Embedded Track #{0}",
        ["AspectFit"] = "Fit",
        ["AspectCrop"] = "Fill",
        ["AspectStretch"] = "Stretch",
        ["VideoLockedMsg"] = "Controls Locked",
        ["VideoUnlockHint"] = "Unlock",
        ["RoomResetAll"] = "Reset Position & Head",

        // In-App Library & Online Metadata Search (Samsung Music + Poweramp + Kodi)
        ["PlayerNowPlaying"] = "Now Playing",
        ["PlayerLibrary"] = "Library",
        ["LibAlbums"] = "Albums",
        ["LibTracks"] = "Tracks",
        ["LibArtists"] = "Artists",
        ["LibFolders"] = "Folders",
        ["LibSearchHint"] = "Search song, album, artist, or folder...",
        ["LibScanning"] = "Scanning device media & spatial folders...",
        ["LibEmpty"] = "No tracks found yet. Grant storage permission, tap Refresh, or use System Picker.",
        ["LibSystemPicker"] = "System Picker",
        ["LibRefresh"] = "Refresh",
        ["LibSongsCount"] = "{0} track(s)",
        ["LibFilterAll"] = "All",
        ["LibFilterSurround"] = "Spatial / Surround",
        ["LibFilterSpatialOnly"] = "Spatial / Atmos Only",
        ["LibEmptyFiltered"] = "No spatial or surround tracks matched the filter.\nTap 'All' to show stereo tracks too.",
        ["LibCacheSynced"] = "{0} track(s) ({1} spatial) • cache ready",
        ["LibIncrementalUpdate"] = "{0} track(s) ({1} spatial) • updated (+{2}/-{3})",
        ["OnlineSearchTitle"] = "Online Cover & Lyrics Search",
        ["OnlineCoverBtn"] = "Find Cover Online",
        ["OnlineLyricsBtn"] = "Find Lyrics Online",
        ["OnlineSubsBtn"] = "Search Online Subtitles",
        ["OnlineAutoBestSwitch"] = "Auto-pick best exact source",
        ["OnlineAutoBestAppliedCover"] = "Exact cover applied ({0}): {1} — {2}",
        ["OnlineAutoBestAppliedLyrics"] = "Exact lyrics applied ({0}): {1} — {2}",
        ["OnlineAutoBestAppliedSub"] = "Exact subtitle applied ({0}): {1}",
        ["OnlineNoExactFallback"] = "No exact source match — pick one below:",
        ["PlayerNoNextTrack"] = "No other track in the current folder/queue.",
        ["PlayerQueuePos"] = "Track {0} of {1}",
        ["OnlineSearching"] = "Searching online...",
        ["OnlineNoResults"] = "No online matches found for this query.",
        ["OnlineAppliedCover"] = "Online cover art applied!",
        ["OnlineAppliedLyrics"] = "Online lyrics applied!",
        ["OnlineAppliedSub"] = "Online subtitle downloaded & active!",
        ["OnlineSearchQueryHint"] = "Artist, song, album, or movie...",
        ["OnlineSearchAction"] = "Search",

        // Player & Room
        ["RoomTitle"] = "3D Room",
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
        ["ExtractDesc"] = "Separate every bed channel and every 3D object into individual WAV files (works with Dolby TrueHD Atmos, E-AC-3 JOC, ADM BWF, DAMF, LAF, WAV, CAF).",
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
        ["TrueHDUnsupported"] = "Dolby TrueHD requires the truehdd decoder. Tap Download to set it up automatically.",
        ["CopyingMedia"] = "Preparing media file...",
        ["DecodingFallback"] = "Decoding audio via Android MediaCodec...",
        ["ProbingContainer"] = "Inspecting container streams with FFmpeg...",
        ["ExtractingTrueHD"] = "Extracting Dolby TrueHD track...",
        ["TrueHDTitle"] = "Dolby TrueHD / Atmos",
        ["TrueHDDecoding"] = "Decoding TrueHD / Atmos with truehdd...",
        ["TrueHDLicenceTitle"] = "truehdd Decoder License (Apache 2.0)",
        ["TrueHDLicenceDesc"] = "Cavernize uses the official static truehdd binary (aarch64-unknown-linux-musl) to decode Dolby TrueHD and Atmos 3D object metadata locally on your device (~1.4 MB download).",
        ["TrueHDDownloadDecode"] = "Accept & Download",
        ["TrueHDCancelled"] = "TrueHD decoding cancelled.",
        ["TrueHDContainerNeedTools"] = "This video/MKV uses an audio format (such as Dolby TrueHD or DTS) that Android cannot decode natively. Install FFmpeg and truehdd in Settings for full support.",
        ["OpenSettingsBtn"] = "Open Settings",

        // Room Stability & Fine-Tuning
        ["LockRoom"] = "Lock Position",
        ["UnlockRoom"] = "Position Locked",
        ["FineTuneBtn"] = "Fine-tune Position & Head",
        ["FineTuneHideBtn"] = "Hide Sliders",
        ["SliderResetHint"] = "Double-tap any slider to reset to its default value",
        ["TrackMultiBadge"] = "Track {0}/{1}",
        ["SelectTrackHeader"] = "Audio Tracks in File",
        ["DiagnosticsTitle"] = "Diagnostics & Shared Logs",
        ["ViewLogBtn"] = "View Log",
        ["ExportLogBtn"] = "Share Log",
        ["ClearLogBtn"] = "Reset Log",
        ["LogClearedMsg"] = "Diagnostic log cleared.",

        // App Auto-Updater
        ["UpdateCardTitle"] = "Software Update",
        ["CurrentVersionLabel"] = "Installed version: v{0}",
        ["CheckUpdateBtn"] = "Check for updates",
        ["CheckingUpdate"] = "Checking GitHub Releases...",
        ["UpdateAvailableBanner"] = "Update available: v{0}",
        ["UpdateAvailableDesc"] = "A newer build of Cavernize is ready ({0:0.1} MB).",
        ["UpToDateMsg"] = "You have the latest version installed.",
        ["DownloadInstallBtn"] = "Download & Install",
        ["DownloadingUpdate"] = "Downloading update...",
        ["InstallReadyBtn"] = "Install Update",
        ["AutoCheckSwitch"] = "Check for updates automatically",
        ["AllowUnknownSourcesMsg"] = "Allow 'Install unknown apps' for Cavernize, then return to complete the installation.",
        ["DismissBtn"] = "Later",

        // FFmpeg Auto-Downloader & Updater
        ["FFmpegCardTitle"] = "FFmpeg Audio Engine",
        ["FFmpegNotInstalled"] = "Not installed (needed for E-AC-3, AC-3, FLAC, Opus, MKV muxing)",
        ["FFmpegInstalledVer"] = "Installed: {0}",
        ["FFmpegUpdateAvail"] = "FFmpeg update available: {0}",
        ["FFmpegDownloadBtn"] = "Download FFmpeg",
        ["FFmpegUpdateBtn"] = "Update FFmpeg",
        ["FFmpegCheckBtn"] = "Check FFmpeg",
        ["FFmpegManualBtn"] = "Locate Manual Binary",
        ["FFmpegDownloading"] = "Downloading static FFmpeg...",
        ["FFmpegUpToDate"] = "FFmpeg is up to date ({0}).",

        // TrueHD Auto-Downloader & Updater
        ["TruehddCardTitle"] = "Dolby TrueHD Engine (truehdd)",
        ["TruehddNotInstalled"] = "Not installed (needed for Dolby TrueHD & TrueHD Atmos playback/stems/export)",
        ["TruehddInstalledVer"] = "Installed: v{0}",
        ["TruehddUpdateAvail"] = "truehdd update available: v{0}",
        ["TruehddDownloadBtn"] = "Download truehdd",
        ["TruehddUpdateBtn"] = "Update truehdd",
        ["TruehddCheckBtn"] = "Check truehdd",
        ["TruehddManualBtn"] = "Locate Manual Binary",
        ["TruehddDownloading"] = "Downloading static truehdd...",
        ["TruehddUpToDate"] = "truehdd is up to date (v{0}).",
        ["TipTruehdd"] = "Official static ARM64 musl build of truehdd/truehdd. Decodes lossless Dolby TrueHD and Atmos 3D objects into DAMF/CAF in private app storage, cleaning temp cache automatically.",

        // One UI 9 Section Titles & Bento Tiles
        ["QuickPanelTitle"] = "Quick Spatial Panel",
        ["BentoHrtf"] = "HRTF 3D",
        ["BentoDist"] = "Ear Distance",
        ["Bento3DOnly"] = "3D Objects",
        ["BentoHeightOnly"] = "Height Only",
        ["TimelineLabel"] = "Playback Position",
        ["CalibTitle"] = "HRIR & QuickEQ Calibration",
        ["AboutVerSection"] = "About & App Version",
        ["RoomAcousticsTitle"] = "Room Acoustics, Gain & Limiter",
        ["SelectFilePill"] = "Open audio or video...",
        ["FileDetailsHint"] = "Technical stream details",
        ["OpenShort"] = "Open",
        ["RoomTouchHint"] = "Drag room to orbit camera • Drag listener • Tap speakers for Solo",

        // Individual Technical Option Tooltips (cause & effect)
        ["TipHrtf"] = "Simulates virtual surround speakers around your head on headphones.\n\n• On: full 3D binaural staging.\n• Off: direct mix without ear HRTF filtering (use on physical speakers).",
        ["TipDistSim"] = "Applies micro-delays and ear cues based on virtual distance.\n\n• On: deeper acoustic room realism.\n• Off: drier soundstage with flatter depth.",
        ["TipRolloff"] = "Sets how fast distant objects drop in volume:\n• Logarithmic / Real (1/d): stronger near-vs-far depth contrast.\n• Linear: gentle uniform decay.\n• Disabled: constant loudness regardless of distance.",
        ["TipMatrixUpmix"] = "Fills surround channels from stereo or 5.1 audio.\n\n• On: widens side and rear staging.\n• Off: preserves only channels present in the source file.",
        ["TipCavernizeUpmix"] = "Synthesizes overhead 3D ceiling objects dynamically.\n\n• On: harmonic and ambient layers lift above ear level.\n• Off: height speakers stay silent unless native Atmos objects exist.",
        ["TipUpmixEffect"] = "Controls extraction aggressiveness during upmixing.\n\n• Higher (%): stronger separation to surrounds and ceiling.\n• Lower (%): preserves closer fidelity to the original stereo/5.1 mix.",
        ["TipUpmixSmooth"] = "Smooths motion speed of synthesized 3D objects.\n\n• Higher (%): calmer, steadier movement without abrupt shifts.\n• Lower (%): fast tracking for sharp transient effects.",
        ["TipRenderGain"] = "Master digital gain offset applied when mixing and exporting.\n\n• Positive (+dB): louder signal (watch for clipping above 0 dB).\n• Negative (-dB): adds headroom to prevent peak distortion.",
        ["TipHeightSqueeze"] = "Compresses elevated objects down toward ear height.\n\n• 0%: full vertical 3D ceiling spread.\n• Near 100%: folds ceiling sounds down onto ground channels.",
        ["TipNormalizer"] = "Dynamic volume control during playback.\n\n• Normalizer On: raises quiet detail and caps harsh peaks.\n• Limiter Only: keeps native loudness untouched, capping peaks at 0 dB.",
        ["TipEnvType"] = "Changes room acoustic scale presets (Home, Concert Hall, Cinema, Studio), shifting perceived wall depth.",
        ["TipHrir"] = "Loads a custom measured Head-Related Impulse Response (.wav) to personalize headphone spatialization.",
        ["TipQuickEQ"] = "Loads EQ calibration filters (.txt from QuickEQ / Equalizer APO) to flatten room or headphone peaks.",
        ["TipSpeakerVirt"] = "Simulates virtual overhead channels across ground speakers when no physical height speakers exist.",
        ["TipMuteBed"] = "Mutes fixed bed channels (5.1 / 7.1) so only dynamic 3D objects stay audible.",
        ["TipMuteGround"] = "Mutes ear-level bed and object audio so only overhead height layers play.",
        ["TipForce24Bit"] = "Forces 24-bit integer PCM instead of 16-bit when exporting lossless WAV/PCM.",
        ["TipSurroundSwap"] = "Swaps Side Surround and Rear Surround channels for receivers using inverted surround mapping.",
        ["TipWavSkip"] = "Omits extended WAV channel masks; improves compatibility with legacy multichannel players.",
        ["TipReportMode"] = "Analyzes objects and dynamics without writing any audio file to disk.",
        ["TipQualityGrading"] = "Calculates dynamic crest factor, object activity, and spatial usage metrics in Post-Render Report.",
        ["TipMlpPres"] = "Selects a specific Dolby TrueHD MLP substream presentation (0–3; -1 picks the richest stream automatically).",
        ["TipPedantic"] = "Enforces strict standard checks; stops immediately on malformed frames.",
        ["TipUnsafe"] = "Bypasses stream safety locks; helps rescue damaged files at higher risk of audio glitches.",
        ["TipRenderTarget"] = "Sets the target speaker configuration or headphone profile used for amplifier wiring and final export mixdown.",
        ["TipApplyRoomExport"] = "When enabled, permanently bakes your 3D room seat position, head orientation, and Solo/Mute edits into the exported file.",
        ["TipExtractModes"] = "• Original Separated: 1 raw mono WAV per bed channel and 3D object.\n• Render Each in 3D: renders each selected item separately in your target 3D layout.\n• 3D Combined: exports all checked 3D objects together.\n• Raw Bitstream: demuxes untouched stream via --extract.",
        ["TipExtractJson"] = "Saves a .json sidecar with frame-by-frame (X, Y, Z) 3D trajectories for dynamic objects.",
        ["TipFFmpegArgs"] = "Additional CLI arguments forwarded directly to FFmpeg when encoding output (e.g. -b:a 960k for higher bitrate).",
    };

    static void ApplyPortuguese(Dictionary<string, string> d) {
        d["OpenFile"] = "Abrir Arquivo";
        d["NoFile"] = "Nenhum arquivo aberto (TrueHD Atmos, EAC3, AC3, MKV, MP4, WAV/ADM, CAF, LAF, ATMOS, MP3/FLAC)";
        d["TrackLabel"] = "Faixa:";
        d["TabMusicPlayer"] = "Player";
        d["TabPlayer"] = "Sala 3D";
        d["TabConvert"] = "Estúdio";
        d["TabExtract"] = "Stems";
        d["TabSettings"] = "Ajustes";

        d["PlayerEmptyTitle"] = "Pronto para ouvir";
        d["PlayerEmptySub"] = "Abra TrueHD Atmos, E-AC-3 Atmos, MKV, MP4, FLAC ou WAV";
        d["PlayerOpenRoom"] = "Personalizar na Sala 3D";
        d["PlayerSwitchTrack"] = "Faixa de Áudio";
        d["PlayerBadgeSpatial"] = "Espacial 3D";
        d["PlayerQuickMatrix"] = "Matrix 7.1";
        d["ModeCover"] = "Capa";
        d["ModeVideo"] = "Vídeo";
        d["ModeLyrics"] = "Letra";
        d["NoLyricsFound"] = "Nenhuma letra encontrada.\nColoque um arquivo .lrc ou .txt com o mesmo nome na pasta da música, ou abra mídia com letra embutida.";
        d["LyricsTapHint"] = "Letra Sincronizada • Toque em um verso para pular";
        d["LyricsUnsyncedHint"] = "Letra Estática (.txt / tags)";
        d["VideoSyncBadge"] = "Vídeo + Áudio 3D • Toque duplo ±10s";
        d["PlayerMoreTitle"] = "Reprodução e Áudio Espacial 3D";
        d["VideoSubtitlesTitle"] = "Legendas (CC)";
        d["SubtitlesOff"] = "Desativar Legendas";
        d["SubtitlesImportBtn"] = "Importar arquivo .srt / .vtt / .ass...";
        d["SubtitlesNoneFound"] = "Nenhuma legenda embutida ou na mesma pasta foi encontrada.";
        d["SubtitlesEmbeddedTag"] = "Faixa Embutida #{0}";
        d["AspectFit"] = "Ajustar";
        d["AspectCrop"] = "Preencher";
        d["AspectStretch"] = "Esticar";
        d["VideoLockedMsg"] = "Tela Travada";
        d["VideoUnlockHint"] = "Destravar";
        d["RoomResetAll"] = "Zerar Posição e Cabeça";

        d["PlayerNowPlaying"] = "Tocando Agora";
        d["PlayerLibrary"] = "Biblioteca";
        d["LibAlbums"] = "Álbuns";
        d["LibTracks"] = "Faixas";
        d["LibArtists"] = "Artistas";
        d["LibFolders"] = "Pastas";
        d["LibSearchHint"] = "Buscar música, álbum, artista ou pasta...";
        d["LibScanning"] = "Escaneando músicas e pastas espaciais...";
        d["LibEmpty"] = "Nenhuma música listada ainda. Toque em Atualizar ou abra pelo Seletor Android.";
        d["LibSystemPicker"] = "Seletor Android";
        d["LibRefresh"] = "Atualizar";
        d["LibSongsCount"] = "{0} faixa(s)";
        d["LibFilterAll"] = "Todas";
        d["LibFilterSurround"] = "Espacial / Surround";
        d["LibFilterSpatialOnly"] = "Só Espacial / Atmos";
        d["LibEmptyFiltered"] = "Nenhuma faixa espacial ou surround neste filtro.\nToque em 'Todas' para exibir faixas estéreo também.";
        d["LibCacheSynced"] = "{0} faixa(s) ({1} espaciais) • cache pronto";
        d["LibIncrementalUpdate"] = "{0} faixa(s) ({1} espaciais) • atualizado (+{2}/-{3})";
        d["OnlineSearchTitle"] = "Buscar Capas e Letras Online";
        d["OnlineCoverBtn"] = "Buscar Capa Online";
        d["OnlineLyricsBtn"] = "Buscar Letra Online";
        d["OnlineSubsBtn"] = "Buscar Legendas Online";
        d["OnlineAutoBestSwitch"] = "Escolher melhor fonte exata auto";
        d["OnlineAutoBestAppliedCover"] = "Capa exata aplicada ({0}): {1} — {2}";
        d["OnlineAutoBestAppliedLyrics"] = "Letra exata aplicada ({0}): {1} — {2}";
        d["OnlineAutoBestAppliedSub"] = "Legenda exata aplicada ({0}): {1}";
        d["OnlineNoExactFallback"] = "Sem correspondência exata — escolha abaixo:";
        d["PlayerNoNextTrack"] = "Não há outra faixa na fila/pasta atual.";
        d["PlayerQueuePos"] = "Faixa {0} de {1}";
        d["OnlineSearching"] = "Pesquisando na internet...";
        d["OnlineNoResults"] = "Nenhum resultado encontrado na internet.";
        d["OnlineAppliedCover"] = "Capa da internet aplicada!";
        d["OnlineAppliedLyrics"] = "Letra aplicada com sucesso!";
        d["OnlineAppliedSub"] = "Legenda online ativada!";
        d["OnlineSearchQueryHint"] = "Música, artista, álbum ou filme...";
        d["OnlineSearchAction"] = "Buscar";

        d["RoomTitle"] = "Sala 3D";
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

        d["ExtractTitle"] = "Extrator de Canais e Objetos 3D";
        d["ExtractDesc"] = "Separe cada canal base e objeto 3D em arquivos WAV individuais (TrueHD Atmos, E-AC-3 JOC, ADM BWF, DAMF, LAF, WAV, CAF).";
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
        d["TrueHDUnsupported"] = "Dolby TrueHD requer o decodificador truehdd. Toque para baixar automaticamente.";
        d["CopyingMedia"] = "Preparando arquivo de mídia...";
        d["DecodingFallback"] = "Decodificando áudio via MediaCodec do Android...";
        d["ProbingContainer"] = "Analisando faixas do contêiner com FFmpeg...";
        d["ExtractingTrueHD"] = "Extraindo faixa Dolby TrueHD...";
        d["TrueHDTitle"] = "Dolby TrueHD / Atmos";
        d["TrueHDDecoding"] = "Decodificando TrueHD / Atmos com truehdd...";
        d["TrueHDLicenceTitle"] = "Licença do Decodificador truehdd (Apache 2.0)";
        d["TrueHDLicenceDesc"] = "O Cavernize usa o binário estático oficial truehdd (aarch64-unknown-linux-musl) para decodificar Dolby TrueHD e metadados 3D Atmos localmente no aparelho (~1,4 MB).";
        d["TrueHDDownloadDecode"] = "Aceitar e Baixar";
        d["TrueHDCancelled"] = "Decodificação TrueHD cancelada.";
        d["TrueHDContainerNeedTools"] = "Este vídeo/MKV usa uma faixa de áudio (como Dolby TrueHD ou DTS) não suportada nativamente pelo Android. Instale o FFmpeg e o truehdd em Ajustes para abrir com suporte completo.";
        d["OpenSettingsBtn"] = "Ir para Ajustes";

        d["LockRoom"] = "Mover Ouvinte";
        d["UnlockRoom"] = "Posição Travada";
        d["FineTuneBtn"] = "Ajuste fino dos eixos";
        d["FineTuneHideBtn"] = "Ocultar ajuste fino";
        d["SliderResetHint"] = "Toque 2x rápido em qualquer slider para restaurar o padrão";
        d["TrackMultiBadge"] = "Faixa {0}/{1}";
        d["SelectTrackHeader"] = "Faixas de Áudio do Arquivo";
        d["DiagnosticsTitle"] = "Diagnóstico e Logs Compartilhados";
        d["ViewLogBtn"] = "Ver Log";
        d["ExportLogBtn"] = "Exportar Log";
        d["ClearLogBtn"] = "Resetar Log";
        d["LogClearedMsg"] = "Log de diagnóstico limpo.";

        d["UpdateCardTitle"] = "Atualização do Aplicativo";
        d["CurrentVersionLabel"] = "Versão instalada: v{0}";
        d["CheckUpdateBtn"] = "Verificar atualizações";
        d["CheckingUpdate"] = "Verificando nova versão no GitHub...";
        d["UpdateAvailableBanner"] = "Nova versão disponível: v{0}";
        d["UpdateAvailableDesc"] = "Há uma atualização pronta para baixar ({0:0.1} MB).";
        d["UpToDateMsg"] = "Seu aplicativo já está na versão mais recente.";
        d["DownloadInstallBtn"] = "Baixar e Instalar";
        d["DownloadingUpdate"] = "Baixando atualização...";
        d["InstallReadyBtn"] = "Instalar Atualização";
        d["AutoCheckSwitch"] = "Verificar atualizações automaticamente ao abrir";
        d["AllowUnknownSourcesMsg"] = "Ative 'Instalar apps desconhecidos' para o Cavernize e volte ao app para instalar automaticamente.";
        d["DismissBtn"] = "Depois";

        d["FFmpegCardTitle"] = "Motor de Áudio FFmpeg";
        d["FFmpegNotInstalled"] = "Não instalado (necessário para E-AC-3, AC-3, FLAC, Opus e MKV)";
        d["FFmpegInstalledVer"] = "Instalado: {0}";
        d["FFmpegUpdateAvail"] = "Atualização do FFmpeg disponível: {0}";
        d["FFmpegDownloadBtn"] = "Baixar FFmpeg Auto";
        d["FFmpegUpdateBtn"] = "Atualizar FFmpeg";
        d["FFmpegCheckBtn"] = "Checar FFmpeg";
        d["FFmpegManualBtn"] = "Selecionar Manualmente";
        d["FFmpegDownloading"] = "Baixando FFmpeg estático ARM64...";
        d["FFmpegUpToDate"] = "FFmpeg atualizado ({0}).";

        d["TruehddCardTitle"] = "Motor Dolby TrueHD (truehdd)";
        d["TruehddNotInstalled"] = "Não instalado (necessário para reproduzir, converter e extrair Stems de Dolby TrueHD / Atmos)";
        d["TruehddInstalledVer"] = "Instalado: v{0}";
        d["TruehddUpdateAvail"] = "Atualização do truehdd disponível: v{0}";
        d["TruehddDownloadBtn"] = "Baixar truehdd Auto";
        d["TruehddUpdateBtn"] = "Atualizar truehdd";
        d["TruehddCheckBtn"] = "Checar truehdd";
        d["TruehddManualBtn"] = "Selecionar Binário Manual";
        d["TruehddDownloading"] = "Baixando truehdd estático ARM64...";
        d["TruehddUpToDate"] = "truehdd atualizado (v{0}).";
        d["TipTruehdd"] = "Binário estático musl oficial do projeto truehdd/truehdd. Decodifica Dolby TrueHD e objetos 3D Atmos em DAMF/CAF direto no armazenamento privado do app, limpando o cache automaticamente.";

        d["QuickPanelTitle"] = "Painel Rápido Espacial";
        d["BentoHrtf"] = "HRTF 3D";
        d["BentoDist"] = "Distância";
        d["Bento3DOnly"] = "Só Obj. 3D";
        d["BentoHeightOnly"] = "Só Teto";
        d["TimelineLabel"] = "Linha do Tempo";
        d["CalibTitle"] = "Calibração HRIR e Filtros QuickEQ";
        d["AboutVerSection"] = "Sobre e Versão do App";
        d["RoomAcousticsTitle"] = "Acústica da Sala, Ganho e Limitador";
        d["SelectFilePill"] = "Abrir arquivo de áudio ou vídeo...";
        d["FileDetailsHint"] = "Detalhes técnicos da faixa";
        d["OpenShort"] = "Abrir";
        d["RoomTouchHint"] = "Arraste o fundo para girar em 3D • Arraste o ouvinte • Toque nas caixas para Solo";

        d["TipHrtf"] = "Simula caixas surround ao redor da cabeça nos fones de ouvido.\n\n• Ativo: cria palco sonoro 3D binaural.\n• Desativado: saída sem filtro HRTF (use ao reproduzir em caixas acústicas físicas).";
        d["TipDistSim"] = "Calcula microatrasos e sombreamento acústico entre os dois ouvidos.\n\n• Ativo: aumenta a sensação realista de profundidade.\n• Desativado: som mais seco e próximo ao centro.";
        d["TipRolloff"] = "Define a rapidez com que sons distantes perdem volume:\n• Logarítmica / Real (1/d): contraste nítido entre objetos próximos e no fundo.\n• Linear: queda suave e uniforme.\n• Desativado: mantém o mesmo volume em toda a sala.";
        d["TipMatrixUpmix"] = "Expande faixas estéreo ou 5.1 preenchendo canais surround 7.1.\n\n• Ativo: abre o palco lateral e traseiro.\n• Desativado: mantém somente os canais originais do arquivo.";
        d["TipCavernizeUpmix"] = "Gera objetos de teto virtuais em 3D a partir de mixagens comuns.\n\n• Ativo: ambiências e camadas sobem para cima do ouvinte.\n• Desativado: caixas de altura tocam apenas quando há objetos Atmos nativos.";
        d["TipUpmixEffect"] = "Força de separação usada ao extrair sons para surround e teto.\n\n• Subir (%): movimentação e altura 3D mais intensas (valores muito altos podem alterar timbres vocais).\n• Descer (%): preserva o equilíbrio original.";
        d["TipUpmixSmooth"] = "Estabiliza a velocidade de deslocamento dos objetos gerados.\n\n• Subir (%): transições suaves e calmas sem saltos rápidos.\n• Descer (%): resposta imediata para efeitos rápidos.";
        d["TipRenderGain"] = "Ajuste mestre de ganho digital aplicado na renderização.\n\n• Subir (+dB): saída mais alta (cuidado com saturação acima de 0 dB).\n• Descer (-dB): cria margem contra distorção em trilhas comprimidas.";
        d["TipHeightSqueeze"] = "Comprime a altura dos sons do teto em direção ao plano horizontal.\n\n• 0%: aproveita toda a elevação vertical até o teto.\n• Perto de 100%: abaixa efeitos de teto para o nível das caixas comuns.";
        d["TipNormalizer"] = "Controle automático de intensidade durante a reprodução.\n\n• Normalizador ativo: equilibra trechos baixos e contém picos.\n• Apenas limitador: não amplifica trechos suaves, só evita clipping acima de 0 dB.";
        d["TipEnvType"] = "Altera a escala acústica virtual da sala (Casa, Sala, Cinema ou Estúdio), modificando distância e reverberação percebida.";
        d["TipHrir"] = "Carrega um arquivo HRIR medido (.wav) para substituir o perfil padrão dos fones pela resposta de outro fone ou ouvido real.";
        d["TipQuickEQ"] = "Aplica curvas de correção acústica (.txt gerados pelo QuickEQ / Equalizer APO) para corrigir picos da sala ou do fone.";
        d["TipSpeakerVirt"] = "Virtualiza canais de teto em sistemas sem caixas físicas acima usando filtragem direcional nas caixas do chão.";
        d["TipMuteBed"] = "Silencia os canais base fixos (5.1/7.1) deixando apenas os objetos dinâmicos 3D audíveis.";
        d["TipMuteGround"] = "Silencia os canais do chão mantendo apenas os sons elevados no plano superior.";
        d["TipForce24Bit"] = "Força exportação em PCM inteiro 24-bit em vez de 16-bit quando compatibilidade sem float é necessária.";
        d["TipSurroundSwap"] = "Troca os canais Side Surround pelos Rear Surround caso seu amplificador ou arquivo use ordem invertida.";
        d["TipWavSkip"] = "Omite a máscara avançada de canais WAV para evitar falhas em players antigos com áudio multicanal.";
        d["TipReportMode"] = "Processa o arquivo apenas para medir objetos, canais e dinâmica no relatório pós-render sem salvar áudio.";
        d["TipQualityGrading"] = "Calcula notas e métricas extras de faixa dinâmica e uso espacial ao terminar o render.";
        d["TipMlpPres"] = "Escolhe qual apresentação interna Dolby TrueHD MLP usar (0 a 3; -1 seleciona a mais completa automaticamente).";
        d["TipPedantic"] = "Exige conformidade estrita do bitstream; interrompe em vez de tentar corrigir pacotes irregulares.";
        d["TipUnsafe"] = "Ignora travas de validação para tentar decodificar arquivos danificados, com risco de falhas sonoras.";
        d["TipRenderTarget"] = "Escolhe o layout físico de caixas ou perfil de fones usado tanto na guia de cabeamento quanto na mixagem exportada.";
        d["TipApplyRoomExport"] = "Quando ativo, grava definitivamente na exportação a posição do ouvinte, inclinação da cabeça e Solos/Mutes da Sala 3D.";
        d["TipExtractModes"] = "• Tudo Separado: extrai 1 WAV mono para cada canal base e objeto 3D.\n• Cada Item em 3D: renderiza cada item selecionado no seu layout 3D.\n• Objetos 3D Combinados: junta apenas os objetos 3D marcados.\n• Bitstream Bruto: extrai a faixa intacta via --extract.";
        d["TipExtractJson"] = "Exporta um arquivo .json com todas as trajetórias 3D (X, Y, Z) quadro a quadro dos objetos.";
        d["TipFFmpegArgs"] = "Argumentos extras passados ao FFmpeg na codificação de saída (ex.: -b:a 960k aumenta o bitrate).";
    }

    static void ApplyHungarian(Dictionary<string, string> d) {
        d["OpenFile"] = "Fájl megnyitása";
        d["TrackLabel"] = "Sáv:";
        d["TabMusicPlayer"] = "Lejátszó";
        d["TabPlayer"] = "3D Szoba";
        d["TabConvert"] = "Konvertálás";
        d["TabExtract"] = "Kivonó";
        d["TabSettings"] = "Sor & Beáll.";
        d["ModeCover"] = "Borító";
        d["ModeVideo"] = "Videó";
        d["ModeLyrics"] = "Dalszöveg";
        d["Play"] = "Lejátszás";
        d["Pause"] = "Szünet";
        d["Stop"] = "Leállítás";
        d["RenderBtn"] = "Renderelés";
        d["AddQueueBtn"] = "Sorhoz adás";
        d["ShowMetaBtn"] = "Kodek metaadatok";
        d["ShowReportBtn"] = "Renderelési jelentés";
        d["WrGui"] = "Bekötési útmutató";
        d["ErrorTitle"] = "Hiba";
        d["UpdateCardTitle"] = "Szoftverfrissítés";
        d["CheckUpdateBtn"] = "Frissítések keresése";
        d["DownloadInstallBtn"] = "Letöltés és telepítés";
        d["LockRoom"] = "Hallgató mozgatása";
        d["UnlockRoom"] = "Pozíció zárolva";
        d["FineTuneBtn"] = "Finomhangolás (csúszkák) ▾";
        d["FineTuneHideBtn"] = "Csúszkák elrejtése ▴";
    }

    static void ApplyChinese(Dictionary<string, string> d) {
        d["OpenFile"] = "打开文件";
        d["TrackLabel"] = "音轨:";
        d["TabMusicPlayer"] = "播放器";
        d["TabPlayer"] = "3D 房间";
        d["TabConvert"] = "转换导出";
        d["TabExtract"] = "分轨提取";
        d["TabSettings"] = "队列与设置";
        d["ModeCover"] = "封面";
        d["ModeVideo"] = "视频";
        d["ModeLyrics"] = "歌词";
        d["Play"] = "播放";
        d["Pause"] = "暂停";
        d["Stop"] = "停止";
        d["RenderBtn"] = "开始渲染";
        d["AddQueueBtn"] = "添加到队列";
        d["ShowMetaBtn"] = "编码器元数据";
        d["ShowReportBtn"] = "渲染后报告";
        d["WrGui"] = "接线指南";
        d["ErrorTitle"] = "错误";
        d["UpdateCardTitle"] = "软件更新";
        d["CheckUpdateBtn"] = "检查更新";
        d["DownloadInstallBtn"] = "下载并安装";
        d["LockRoom"] = "自由移动位置";
        d["UnlockRoom"] = "位置已锁定";
        d["FineTuneBtn"] = "微调位置与头部朝向 ▾";
        d["FineTuneHideBtn"] = "隐藏微调滑块 ▴";
    }
}
