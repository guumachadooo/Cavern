using Android;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.External;
using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

using CavernizeAndroid.Audio;
using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using VoidX.WPF.FFmpeg;

using Path = System.IO.Path;

namespace CavernizeAndroid;


public partial class MainActivity {
    void LoadUriAsync(Android.Net.Uri uri) {
        if (isOperationRunning || isPreparingTrueHD) {
            ShowError(S["OpRunning"]);
            return;
        }
        AppLogger.Info($"Loading URI: {uri}");
        currentMediaUri = uri;
        SetConvertStatus(S["CopyingMedia"], -1);
        SetWorkspaceHeaderText(S["CopyingMedia"]);
        Task.Run(() => {
            try {
                string resolved = AndroidFileHelper.ResolveReadablePath(this, uri,
                    (p, name) => RunOnUiThread(() => {
                        string msg = $"{S["CopyingMedia"]} ({name}: {p:0%})";
                        SetWorkspaceHeaderText(msg);
                        SetConvertStatus(msg, p);
                    }));
                AppLogger.Info($"Resolved file path: '{resolved}'");

                MediaArtworkMetadata extractedMeta = MediaMetadataHelper.Extract(
                    this, uri, resolved, Path.GetFileName(resolved));

                AudioFile file = OpenAudioFileResilient(resolved, out string newFallbackSessionDir);

                RunOnUiThread(() => {
                    string queueSeed = !string.IsNullOrEmpty(extractedMeta?.SourceMediaPath) && File.Exists(extractedMeta.SourceMediaPath)
                        ? extractedMeta.SourceMediaPath
                        : resolved;
                    EnsureActiveQueueForCurrentFile(queueSeed);
                    string oldFallback = activeFallbackSessionDir;
                    activeFallbackSessionDir = newFallbackSessionDir;
                    OpenContent(file, extractedMeta);
                    if (!string.IsNullOrEmpty(oldFallback) &&
                        !string.Equals(oldFallback, newFallbackSessionDir, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldFallback)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldFallback);
                    }
                    TrueHDDecoderService.CleanupOrphanedCache(this, CollectActiveProtectedFiles(resolved));
                    SetConvertStatus(S["Ready"], 0);
                });
            } catch (Exception ex) {
                AppLogger.Error("LoadUriAsync", ex);
                RunOnUiThread(() => {
                    SetWorkspaceHeaderText(LoadedFile != null ? Path.GetFileName(LoadedFile.Path) : S["SelectFilePill"]);
                    SetConvertStatus(ex.Message, 0);
                    ShowLoadFailureDialog(ex);
                });
            }
        });
    }

    static bool HasVideoOnlyTrackViaMediaExtractor(string resolved) {
        if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved)) {
            return false;
        }
        Android.Media.MediaExtractor extractor = null;
        try {
            extractor = new Android.Media.MediaExtractor();
            extractor.SetDataSource(resolved);
            bool hasVideo = false;
            bool hasAudio = false;
            for (int i = 0; i < extractor.TrackCount; i++) {
                string mime = extractor.GetTrackFormat(i)?.GetString(Android.Media.MediaFormat.KeyMime) ?? "";
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) {
                    hasAudio = true;
                } else if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) {
                    hasVideo = true;
                }
            }
            return hasVideo && !hasAudio;
        } catch {
            return false;
        } finally {
            try { extractor?.Release(); } catch { }
            try { extractor?.Dispose(); } catch { }
        }
    }

    AudioFile OpenAudioFileResilient(string resolved, out string fallbackSessionDir) {
        fallbackSessionDir = null;
        string ext = Path.GetExtension(resolved);
        if (ext.Equals(".atmos", StringComparison.OrdinalIgnoreCase)) {
            if (!File.Exists(resolved + ".audio")) {
                throw new InvalidDataException(S["DamfMissingAudioError"]);
            }
            // DAMF (.atmos + .atmos.audio) must never enter FFmpeg/MediaCodec video/audio fallbacks
            return new AudioFile(resolved);
        }

        try {
            AudioFile file = new(resolved);
            if (file.Tracks.Count > 0 && file.Tracks.Any(t => t.Supported)) {
                return file;
            }
            file.Dispose();
            throw new NotSupportedException("Fallback required for container/codec");
        } catch (Exception nativeEx) {
            AppLogger.Info($"Native Cavern reader fallback ({nativeEx.GetType().Name}): inspecting container/codec...");
            return TryOpenWithSmartFallbacks(resolved, out fallbackSessionDir);
        }
    }

    AudioFile TryOpenWithSmartFallbacks(string resolved, out string fallbackSessionDir) {
        fallbackSessionDir = null;
        if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved) || new FileInfo(resolved).Length < 16) {
            throw new InvalidOperationException(S["MediaDecodeFailedCorrupt"]);
        }
        string containerExt = Path.GetExtension(resolved).ToLowerInvariant();
        Container guessedContainer = containerExt switch {
            ".mkv" or ".mka" or ".webm" or ".weba" => Container.Matroska,
            ".mp4" or ".m4a" or ".m4v" or ".mov" or ".qt" => Container.MP4,
            ".mxf" => Container.MXF,
            _ => Container.NotContainer
        };

        ContainerProbeResult probe = null;
        if (ffmpeg.Found) {
            RunOnUiThread(() => {
                SetWorkspaceHeaderText(S["ProbingContainer"]);
                SetConvertStatus(S["ProbingContainer"], -1);
            });
            probe = TrueHDDecoderService.ProbeContainerWithFFmpeg(ffmpeg.Location, resolved);
            ProbedAudioStream spatialStream = probe.AudioStreams.FirstOrDefault(s => s.IsTrueHD)
                ?? probe.AudioStreams.FirstOrDefault(s => s.IsEnhancedAC3)
                ?? probe.AudioStreams.FirstOrDefault(s => s.IsAC3);

            if (spatialStream != null) {
                string workDir = Path.Combine(TrueHDDecoderService.GetWorkRoot(this), $"probe_{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                fallbackSessionDir = workDir;

                if (spatialStream.IsTrueHD) {
                    RunOnUiThread(() => {
                        SetWorkspaceHeaderText(S["ExtractingTrueHD"]);
                        SetConvertStatus(S["ExtractingTrueHD"], -1);
                    });
                    string rawThd = Path.Combine(workDir, "extracted.thd");
                    bool ok = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                        ffmpeg.Location, resolved, spatialStream.StreamIndex, rawThd, "truehd",
                        probe.DurationSeconds,
                        (p, msg) => RunOnUiThread(() => {
                            SetWorkspaceHeaderText(msg);
                            SetConvertStatus(msg, p);
                        }));
                    if (ok) {
                        ExternalPendingTrack pending = new(
                            resolved, rawThd, Codec.TrueHD, guessedContainer,
                            spatialStream.AudioOrdinal, spatialStream.Language,
                            spatialStream.SampleRate, probe.DurationSeconds, spatialStream.ChannelSummary);
                        return AudioFile.FromCustomTracks(resolved, [pending]);
                    }
                } else {
                    string fmt = spatialStream.IsEnhancedAC3 ? "eac3" : "ac3";
                    string ext = "." + fmt;
                    string rawPath = Path.Combine(workDir, "extracted" + ext);
                    bool ok = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                        ffmpeg.Location, resolved, spatialStream.StreamIndex, rawPath, fmt,
                        probe.DurationSeconds,
                        (p, msg) => RunOnUiThread(() => {
                            SetWorkspaceHeaderText(msg);
                            SetConvertStatus(msg, p);
                        }));
                    if (ok) {
                        AudioReader extractedReader = AudioReader.Open(rawPath);
                        Codec c = spatialStream.IsEnhancedAC3 ? Codec.EnhancedAC3 : Codec.AC3;
                        ExternalDecodedTrack decoded = new(
                            extractedReader, c, c, null, guessedContainer, null, rawPath, spatialStream.Language);
                        return AudioFile.FromCustomTracks(resolved, [decoded]);
                    }
                }
            }
        }

        if ((probe == null && HasVideoOnlyTrackViaMediaExtractor(resolved)) ||
            (probe != null && probe.AudioStreams.Count == 0 && guessedContainer != Container.NotContainer &&
             probe.RawOutput != null && probe.RawOutput.Contains("Video:", StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidOperationException(S["VideoNoAudioTrack"]);
        }
        if (probe != null && probe.AudioStreams.Count == 0 && !string.IsNullOrWhiteSpace(probe.RawOutput) &&
            (probe.RawOutput.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) ||
             probe.RawOutput.Contains("could not find codec", StringComparison.OrdinalIgnoreCase) ||
             probe.RawOutput.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase) ||
             probe.RawOutput.Contains("End of file", StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidOperationException(S["MediaDecodeFailedCorrupt"]);
        }
        if (probe != null && probe.AudioStreams.Count == 0 && guessedContainer != Container.NotContainer) {
            throw new InvalidOperationException(S["VideoNoAudioTrack"]);
        }

        // Fast native NEON FFmpeg path for FLAC, ALAC (.m4a), and other lossless/MediaCodec-unsupported codecs
        // (avoids slow MediaCodec decode / audio/unknown IllegalArgumentException and preserves 24-bit PCM depth)
        ProbedAudioStream fastStream = probe != null && probe.AudioStreams.Count > 0 ? probe.AudioStreams[0] : null;
        string fastCodec = (fastStream?.CodecName ?? string.Empty).ToLowerInvariant();
        bool preferFastFfmpeg = containerExt == ".flac" ||
                                fastCodec == "flac" ||
                                fastCodec == "alac" ||
                                fastCodec == "ape" ||
                                fastCodec == "wavpack" ||
                                fastCodec == "wv" ||
                                fastCodec == "tta" ||
                                fastCodec == "dts" ||
                                fastCodec == "mlp" ||
                                fastCodec.StartsWith("pcm_", StringComparison.Ordinal);
        if (preferFastFfmpeg && ffmpeg.Found) {
            int streamIdx = fastStream?.StreamIndex ?? -1;
            string workDir = Path.Combine(TrueHDDecoderService.GetWorkRoot(this), $"ffwav_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            fallbackSessionDir = workDir;
            string outWav = Path.Combine(workDir, "decoded.wav");
            RunOnUiThread(() => {
                SetWorkspaceHeaderText(S["DecodingFallback"]);
                SetConvertStatus(S["DecodingFallback"], -1);
            });
            bool ffOk = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                ffmpeg.Location, resolved, streamIdx, outWav, "wav",
                probe?.DurationSeconds ?? 0,
                (p, msg) => RunOnUiThread(() => {
                    SetWorkspaceHeaderText(msg);
                    SetConvertStatus(msg, p);
                }));
            if (ffOk) {
                AudioReader wavReader = AudioReader.Open(outWav);
                Codec pcmCodec = wavReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE;
                (Codec srcCodec, bool isLossy) = InferFallbackCodecAndLossy(resolved, fastStream?.CodecName);
                ExternalDecodedTrack wavTrack = new(
                    wavReader, pcmCodec, srcCodec, null, guessedContainer, null, null, fastStream?.Language,
                    isLossySource: isLossy, originalSourcePath: resolved);
                return AudioFile.FromCustomTracks(resolved, [wavTrack]);
            }
            TrueHDDecoderService.CleanupWorkDirectory(workDir);
            fallbackSessionDir = null;
        }

        // Standard Android MediaCodec fallback (AAC, MP3, OGG, Opus, etc., or FLAC if FFmpeg failed)
        RunOnUiThread(() => {
            SetWorkspaceHeaderText(S["DecodingFallback"]);
            SetConvertStatus(S["DecodingFallback"], -1);
        });
        string wavCacheDir = Path.Combine(CacheDir.AbsolutePath, "decoded_fallback");
        try {
            string decodedWav = AndroidMediaFallbackDecoder.DecodeToWav(resolved, wavCacheDir,
                p => RunOnUiThread(() => {
                    string msg = $"{S["DecodingFallback"]} ({p:0%})";
                    SetWorkspaceHeaderText(msg);
                    SetConvertStatus(msg, p);
                }));
            AudioReader fallbackReader = AudioReader.Open(decodedWav);
            Codec pcmCodec = fallbackReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE;
            string probedCodecName = probe != null && probe.AudioStreams.Count > 0 ? probe.AudioStreams[0].CodecName : null;
            string probedLang = probe != null && probe.AudioStreams.Count > 0 ? probe.AudioStreams[0].Language : null;
            (Codec srcCodec, bool isLossy) = InferFallbackCodecAndLossy(resolved, probedCodecName);
            ExternalDecodedTrack fallbackTrack = new(
                fallbackReader, pcmCodec, srcCodec, null, guessedContainer, null, null, probedLang,
                isLossySource: isLossy, originalSourcePath: resolved);
            return AudioFile.FromCustomTracks(resolved, [fallbackTrack]);
        } catch (Exception mediaEx) {
            AppLogger.Warn($"Android MediaCodec fallback failed ({mediaEx.GetType().Name}): {mediaEx.Message}");

            // Final universal fallback: decode audio stream via FFmpeg to WAV (even when container probe returned 0 explicit streams)
            if (ffmpeg.Found) {
                ProbedAudioStream firstAudio = probe != null && probe.AudioStreams.Count > 0 ? probe.AudioStreams[0] : null;
                int streamIdx = firstAudio?.StreamIndex ?? -1;
                string workDir = Path.Combine(TrueHDDecoderService.GetWorkRoot(this), $"ffwav_{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                fallbackSessionDir = workDir;
                string outWav = Path.Combine(workDir, "decoded.wav");
                RunOnUiThread(() => {
                    SetWorkspaceHeaderText(S["DecodingFallback"]);
                    SetConvertStatus(S["DecodingFallback"], -1);
                });
                bool wavOk = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                    ffmpeg.Location, resolved, streamIdx, outWav, "wav",
                    probe?.DurationSeconds ?? 0,
                    (p, msg) => RunOnUiThread(() => {
                        SetWorkspaceHeaderText(msg);
                        SetConvertStatus(msg, p);
                    }));
                if (wavOk) {
                    AudioReader wavReader = AudioReader.Open(outWav);
                    Codec pcmCodec = wavReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE;
                    (Codec srcCodec, bool isLossy) = InferFallbackCodecAndLossy(resolved, firstAudio?.CodecName);
                    ExternalDecodedTrack wavTrack = new(
                        wavReader, pcmCodec, srcCodec, null, guessedContainer, null, null, firstAudio?.Language,
                        isLossySource: isLossy, originalSourcePath: resolved);
                    return AudioFile.FromCustomTracks(resolved, [wavTrack]);
                }
            }

            if (!ffmpeg.Found && containerExt is ".mkv" or ".mka" or ".m2ts" or ".ts" or ".mp4" or ".thd" or ".mlp" or ".truehd") {
                throw new NotSupportedException(S["TrueHDContainerNeedTools"], mediaEx);
            }
            throw new InvalidOperationException(S["MediaDecodeFailedCorrupt"], mediaEx);
        }
    }

    static (Codec SourceCodec, bool IsLossy) InferFallbackCodecAndLossy(string sourcePath, string ffCodecName) {
        string token = (ffCodecName ?? "").Trim().ToLowerInvariant();
        if (token.Contains("flac")) return (Codec.FLAC, false);
        if (token.Contains("truehd") || token.Contains("mlp")) return (Codec.TrueHD, false);
        if (token.Contains("alac") || token.StartsWith("pcm")) return (Codec.PCM_LE, false);
        if (token.Contains("eac3")) return (Codec.EnhancedAC3, true);
        if (token.Contains("ac3")) return (Codec.AC3, true);
        if (token.Contains("dts")) return (token.Contains("hd") || token.Contains("ma") ? Codec.DTS_HD : Codec.DTS, !(token.Contains("hd") || token.Contains("ma")));
        if (token.Contains("aac") || token.Contains("mp3") || token.Contains("opus") || token.Contains("vorbis") || token.Contains("wma")) {
            return (Codec.Unknown, true);
        }

        string ext = Path.GetExtension(sourcePath ?? "").ToLowerInvariant();
        return ext switch {
            ".flac" => (Codec.FLAC, false),
            ".wav" or ".aif" or ".aiff" or ".caf" => (Codec.PCM_LE, false),
            ".laf" => (Codec.LimitlessAudio, false),
            ".mp3" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma" => (Codec.Unknown, true),
            ".ac3" => (Codec.AC3, true),
            ".ec3" or ".eac3" => (Codec.EnhancedAC3, true),
            ".dts" => (Codec.DTS, true),
            _ => (Codec.Unknown, true)
        };
    }

    void ShowLoadFailureDialog(Exception ex) {
        if (uiDestroyed) {
            return;
        }
        bool suggestSettings = !ffmpeg.Found || string.IsNullOrEmpty(TruehddInstallerService.ResolveExistingTruehddPath(this, store));
        if (suggestSettings) {
            OneUiModalDialog.Show(
                this,
                S["ErrorTitle"],
                ex.Message,
                icon: OneUiIconType.Info,
                iconBadgeColor: ColDanger,
                positiveText: S["OpenSettingsBtn"],
                onPositive: () => JumpToSettingsCategory(4),
                negativeText: S["DismissBtn"]);
        } else {
            ShowError(ex.Message);
        }
    }

    // ICavernizeApp implementation
    public void OpenContent(string path) {
        Reset();
        ffmpeg.CheckFFmpeg();
        MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, path, Path.GetFileName(path));
        AudioFile file = OpenAudioFileResilient(path, out string fallbackDir);
        if (!string.IsNullOrEmpty(fallbackDir)) {
            if (suppressAutoTrueHDPrepare) {
                protectedQueuePaths.Add(Path.GetFullPath(fallbackDir));
            } else {
                activeFallbackSessionDir = fallbackDir;
            }
        }
        OpenContent(file, meta);
    }

    void OpenLocalPathWithFallbackAsync(string path, int? switchToTab = null, bool autoPlay = false, bool bypassBusyGuard = false) {
        if (!bypassBusyGuard && (isOperationRunning || isPreparingTrueHD)) {
            ShowError(S["OpRunning"]);
            return;
        }
        currentMediaUri = null;
        SetConvertStatus(S["CopyingMedia"], -1);
        SetWorkspaceHeaderText(Path.GetFileName(path));
        Task.Run(() => {
            try {
                MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, path, Path.GetFileName(path));
                AudioFile file = OpenAudioFileResilient(path, out string newFallbackSessionDir);
                RunOnUiThread(() => {
                    EnsureActiveQueueForCurrentFile(path);
                    string oldFallback = activeFallbackSessionDir;
                    activeFallbackSessionDir = newFallbackSessionDir;
                    pendingAutoPlayAfterLoad = autoPlay;
                    OpenContent(file, meta);
                    pendingAutoPlayAfterLoad = false;
                    if (!string.IsNullOrEmpty(oldFallback) &&
                        !string.Equals(oldFallback, newFallbackSessionDir, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldFallback)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldFallback);
                    }
                    SetConvertStatus(S["Ready"], 0);
                    if (switchToTab.HasValue) {
                        SwitchTab(switchToTab.Value);
                    }
                });
            } catch (Exception ex) {
                pendingSeekAfterLoadSeconds = -1;
                bool isExpectedMediaReject = ex is InvalidOperationException &&
                    (string.Equals(ex.Message, S["VideoNoAudioTrack"], StringComparison.Ordinal) ||
                     string.Equals(ex.Message, S["MediaDecodeFailedCorrupt"], StringComparison.Ordinal));
                if (isExpectedMediaReject) {
                    AppLogger.Warn($"OpenLocalPathWithFallbackAsync rejected '{Path.GetFileName(path)}': {ex.Message}");
                } else {
                    AppLogger.Error("OpenLocalPathWithFallbackAsync", ex);
                }
                RunOnUiThread(() => {
                    pendingAutoPlayAfterLoad = false;
                    SetWorkspaceHeaderText(LoadedFile != null ? Path.GetFileName(LoadedFile.Path) : S["SelectFilePill"]);
                    SetConvertStatus(ex.Message, 0);
                    ShowLoadFailureDialog(ex);
                });
            }
        });
    }

    void SwitchActiveTrackVariant(LibraryTrackItem targetVariant, IReadOnlyList<LibraryTrackItem> allVariants) {
        if (targetVariant == null || string.IsNullOrEmpty(targetVariant.FilePath)) {
            return;
        }
        if (LoadedFile != null && string.Equals(LoadedFile.Path, targetVariant.FilePath, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        int myGen = ++variantSwitchGeneration;
        bool wasPlaying = playerEngine?.IsPlaying ?? false;
        double baseSec = (playerEngine != null && playerEngine.TotalSeconds > 0) ? playerEngine.CurrentSeconds : 0;
        long startElapsedMs = SystemClock.ElapsedRealtime();
        preferredAudioCategory = AudioQualityAnalyzer.ClassifyCategory(targetVariant);
        preferredFormatVariantKey = MediaLibraryService.GetDistinctVariantLabel(targetVariant, allVariants);

        if (activeLibraryQueueIndex >= 0 && activeLibraryQueueIndex < activeLibraryQueue.Count) {
            activeLibraryQueue[activeLibraryQueueIndex] = targetVariant;
        }

        string targetPath = targetVariant.FilePath;
        string prevTitle = currentMediaMeta?.Title;
        MediaArtworkMetadata prevMetaSnap = currentMediaMeta;

        // Keep current track playing seamlessly in the background while loading & decoding the target variant
        Task.Run(() => {
            AudioFile newFile = null;
            string newFallbackDir = null;
            ExternalDecodedTrack newDecodedTrueHD = null;
            string newTrueHDSession = null;
            try {
                MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, targetPath, Path.GetFileName(targetPath));
                newFile = OpenAudioFileResilient(targetPath, out newFallbackDir);
                if (newFile == null || newFile.Tracks.Count == 0) {
                    throw new InvalidOperationException("No playable audio tracks in selected variant.");
                }
                CavernizeTrack bestTrack = newFile.Tracks
                    .Where(x => x.Codec != Codec.Unknown && x.Supported)
                    .OrderBy(x => x.Codec)
                    .FirstOrDefault() ?? newFile.Tracks[0];

                CavernizeTrack readyTrack = bestTrack;
                if (bestTrack.Codec == Codec.TrueHD) {
                    string truehddPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
                    if (string.IsNullOrEmpty(truehddPath) || !File.Exists(truehddPath) || !store.TruehddLicenceAccepted) {
                        // Needs interactive truehdd download/licence modal -> fall back cleanly with elapsed-time compensation
                        newFile.Dispose();
                        if (!string.IsNullOrEmpty(newFallbackDir)) {
                            TrueHDDecoderService.CleanupWorkDirectory(newFallbackDir);
                        }
                        RunOnUiThread(() => {
                            if (uiDestroyed || myGen != variantSwitchGeneration) return;
                            double liveSec = (wasPlaying && playerEngine.IsPlaying)
                                ? playerEngine.CurrentSeconds
                                : (wasPlaying ? baseSec + (SystemClock.ElapsedRealtime() - startElapsedMs) / 1000.0 : baseSec);
                            pendingSeekAfterLoadSeconds = liveSec > 0.25 ? liveSec : -1;
                            ShowToast(string.Format(S["FormatSwitchedToast"], preferredFormatVariantKey));
                            OpenLocalPathWithFallbackAsync(targetPath, switchToTab: null, autoPlay: wasPlaying, bypassBusyGuard: true);
                        });
                        return;
                    }

                    int? forcedPres = store.MlpPresentation >= 0 ? store.MlpPresentation : null;
                    TrueHDDecodeResult decRes = TrueHDDecoderService.DecodeTrack(
                        this,
                        bestTrack,
                        newFile.Path,
                        ffmpeg.Found ? ffmpeg.Location : null,
                        truehddPath,
                        forcedPres,
                        null,
                        CancellationToken.None);
                    newDecodedTrueHD = decRes.DecodedTrack;
                    newTrueHDSession = decRes.SessionDirectory;
                    readyTrack = newDecodedTrueHD;
                }

                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != variantSwitchGeneration || readyTrack == null) {
                        newDecodedTrueHD?.Dispose();
                        newFile?.Dispose();
                        if (!string.IsNullOrEmpty(newTrueHDSession)) TrueHDDecoderService.CleanupWorkDirectory(newTrueHDSession);
                        if (!string.IsNullOrEmpty(newFallbackDir)) TrueHDDecoderService.CleanupWorkDirectory(newFallbackDir);
                        return;
                    }

                    // Preserve high-res artwork & synced lyrics from current song to prevent visual flash
                    bool sameSong = prevMetaSnap != null &&
                        (string.Equals(prevTitle, meta?.Title, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(meta?.Title));
                    if (meta != null && sameSong) {
                        if (!meta.HasEmbeddedArtwork && IsBitmapUsable(prevMetaSnap.ArtworkBitmap)) {
                            meta.ArtworkBitmap = prevMetaSnap.ArtworkBitmap;
                            meta.HasEmbeddedArtwork = prevMetaSnap.HasEmbeddedArtwork;
                            meta.PrimaryAccent = prevMetaSnap.PrimaryAccent;
                            meta.SecondaryAccent = prevMetaSnap.SecondaryAccent;
                        }
                        if ((meta.Lyrics == null || !meta.Lyrics.HasLyrics) && prevMetaSnap.Lyrics?.HasLyrics == true) {
                            meta.Lyrics = prevMetaSnap.Lyrics;
                        }
                        if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = prevMetaSnap.Title;
                        if (string.IsNullOrWhiteSpace(meta.Artist)) meta.Artist = prevMetaSnap.Artist;
                        if (string.IsNullOrWhiteSpace(meta.Album)) meta.Album = prevMetaSnap.Album;
                    }

                    // Exact real-time elapsed timestamp so playback NEVER jumps backward
                    double liveSec = (wasPlaying && playerEngine.IsPlaying)
                        ? playerEngine.CurrentSeconds
                        : (wasPlaying ? baseSec + (SystemClock.ElapsedRealtime() - startElapsedMs) / 1000.0 : baseSec);

                    AudioFile oldFile = LoadedFile;
                    ExternalDecodedTrack oldDecoded = decodedTrueHDTrack;
                    string oldTrueHDSession = activeTrueHDSessionDir;
                    string oldFallback = activeFallbackSessionDir;

                    bool swapped = false;
                    if (readyTrack.Supported && readyTrack.Codec != Codec.TrueHD) {
                        try {
                            RecordActiveTrackMeasuredLoudnessIfValid();
                            double crossOffset = ComputeCrossVariantNeuralOffsetSamples(readyTrack, meta?.SourceMediaPath ?? targetPath);
                            playerEngine.HotSwapTrackSeamless(
                                readyTrack,
                                liveSec,
                                upmixingSettings,
                                renderingSettings,
                                preserveNeuralStems: sameSong,
                                neuralReferenceOffset: crossOffset);
                            PrimeEngineLoudnessForPath(meta?.SourceMediaPath ?? targetPath, meta);
                            swapped = true;
                        } catch (Exception swapEx) {
                            AppLogger.Warn($"HotSwapTrackSeamless fallback: {swapEx.Message}");
                            swapped = false;
                        }
                    }

                    if (!swapped) {
                        // Safe fallback if seamless swap couldn't initialize
                        pendingSeekAfterLoadSeconds = liveSec > 0.25 ? liveSec : -1;
                        pendingAutoPlayAfterLoad = wasPlaying;
                        activeFallbackSessionDir = newFallbackDir;
                        OpenContent(newFile, meta);
                        pendingAutoPlayAfterLoad = false;
                        return;
                    }

                    // Update active references without stopping AudioTrack or tearing down playback
                    LoadedFile = newFile;
                    selectedTrack = bestTrack;
                    decodedTrueHDTrack = newDecodedTrueHD;
                    activeTrueHDSessionDir = newTrueHDSession;
                    activeFallbackSessionDir = newFallbackDir;
                    currentMediaMeta = meta;
                    currentMediaUri = targetVariant.ContentUri;
                    SetLiveTrackSourceItems();

                    // Keep current stage mode (Cover or Lyrics) intact without flashing
                    if (sameSong && meta?.HasVideo != true && musicStageMode != PlayerStageMode.Video) {
                        lastStageBoundPath = meta?.SourceMediaPath ?? newFile.Path ?? "";
                    }

                    if (wasPlaying && !playerEngine.IsPlaying) {
                        playerEngine.IsPlaying = true;
                        audioOutput.Start();
                    }
                    if (meta?.HasVideo == true) {
                        musicVideoStage?.SeekTo(liveSec);
                    }

                    EnsureActiveQueueForCurrentFile(targetPath);
                    SetWorkspaceHeaderText(!string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title : Path.GetFileName(newFile.Path));
                    string details = string.Join(" | ", readyTrack.Details.Select(d => $"{d.property}: {d.value}"));
                    SetWorkspaceSummaryText($"{readyTrack.FormatHeader}\n{details}");

                    mediaNotification?.UpdateMetadata(
                        !string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title : Path.GetFileNameWithoutExtension(newFile.Path),
                        !string.IsNullOrWhiteSpace(meta?.Artist) ? meta.Artist : $"Cavernize 3D • {readyTrack.FormatHeader}",
                        !string.IsNullOrWhiteSpace(meta?.Album) ? meta.Album : readyTrack.FormatHeader,
                        playerEngine.TotalSeconds,
                        meta?.ArtworkBitmap);

                    RebuildInlineTrackList();
                    RefreshPlayerSourcesList();
                    RefreshMusicPlayerUi();
                    RefreshExtractorChecklist();
                    RefreshConverterTrackInfo();
                    RefreshContainerOptions();
                    SyncPlaybackUi(playerEngine.IsPlaying, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
                    UpdateRoomCanvas();
                    SyncNeuralReferenceOffsetForActiveTrack(readyTrack);
                    if (!(playerEngine?.NeuralStage?.HasPlayableSeparatedStems ?? false)) {
                        TryAutoBindCachedNeuralStemsAsync(readyTrack, startupDelayMs: wasPlaying ? 350 : 0);
                    }
                    StartLosslessTwinDiscoveryAsync(readyTrack, startupDelayMs: wasPlaying ? 900 : 0);
                    RefreshNeuralAudioUi();
                    RefreshNeuralPrepUi();
                    RefreshLyricsSingDockUi();
                    RefreshBottomActionBarVisuals();
                    ShowToast(string.Format(S["FormatSwitchedToast"], preferredFormatVariantKey));

                    // Clean up previous file/session resources AFTER engine has atomically switched
                    if (oldDecoded != null && oldDecoded != newDecodedTrueHD && !IsSessionProtectedByQueue(oldTrueHDSession)) {
                        try { oldDecoded.Dispose(); } catch { }
                    }
                    if (oldFile != null && oldFile != newFile && renderQueue.Jobs.FirstOrDefault(x => x.IsUsingFile(oldFile)) == null) {
                        try { oldFile.Dispose(); } catch { }
                    }
                    if (!string.IsNullOrEmpty(oldTrueHDSession) && !string.Equals(oldTrueHDSession, newTrueHDSession, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldTrueHDSession)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldTrueHDSession);
                    }
                    if (!string.IsNullOrEmpty(oldFallback) && !string.Equals(oldFallback, newFallbackDir, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldFallback)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldFallback);
                    }
                });
            } catch (Exception ex) {
                newDecodedTrueHD?.Dispose();
                newFile?.Dispose();
                if (!string.IsNullOrEmpty(newTrueHDSession)) TrueHDDecoderService.CleanupWorkDirectory(newTrueHDSession);
                if (!string.IsNullOrEmpty(newFallbackDir)) TrueHDDecoderService.CleanupWorkDirectory(newFallbackDir);
                AppLogger.Error("SwitchActiveTrackVariant", ex);
                RunOnUiThread(() => {
                    if (!uiDestroyed && myGen == variantSwitchGeneration) {
                        ShowLoadFailureDialog(ex);
                    }
                });
            }
        });
    }

    public void OpenContent(AudioFile file) => OpenContent(file, null);

    void OpenContent(AudioFile file, MediaArtworkMetadata meta) {
        StopPlayback();
        MediaArtworkMetadata prevMeta = currentMediaMeta;
        currentMediaMeta = meta ?? (!string.IsNullOrEmpty(file?.Path)
            ? MediaMetadataHelper.Extract(this, null, file.Path, Path.GetFileName(file.Path))
            : null);

        LoadedFile = file;
        SetWorkspaceHeaderText(!string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileName(file.Path));
        AppLogger.Info($"Opened file: '{file.Path}', tracks={file.Tracks.Count}, embeddedArt={currentMediaMeta?.HasEmbeddedArtwork}");

        if (file.Tracks.Count > 0) {
            CavernizeTrack best = file.Tracks
                .Where(x => x.Codec != Codec.Unknown && x.Supported)
                .OrderBy(x => x.Codec)
                .FirstOrDefault() ?? file.Tracks[0];
            SelectedTrack = best;
        } else {
            SelectedTrack = null;
        }

        if (prevMeta != null && prevMeta != currentMediaMeta &&
            prevMeta.ArtworkBitmap != null && prevMeta.ArtworkBitmap != currentMediaMeta?.ArtworkBitmap) {
            prevMeta.ArtworkBitmap.Dispose();
            prevMeta.ArtworkBitmap = null;
        }
        discoveredSubtitles = [];
        activeSubtitleTrackId = null;
        activeSubtitleCues = [];
        musicVideoStage?.SetSubtitles(activeSubtitleCues);
        if (currentMediaMeta?.HasVideo == true) {
            StartBackgroundSubtitleDiscovery(file?.Path);
        }
        StartBackgroundOnlineEnrichment(currentMediaMeta);
        RefreshMusicPlayerUi();
    }

    void StartBackgroundSubtitleDiscovery(string fallbackPath) {
        string mediaPath = currentMediaMeta?.SourceMediaPath;
        if (string.IsNullOrEmpty(mediaPath)) mediaPath = fallbackPath;
        string title = currentMediaMeta?.Title ?? "";
        Android.Net.Uri uriSnap = currentMediaUri;
        Task.Run(() => {
            List<SubtitleTrackInfo> tracks = SubtitleService.DiscoverTracks(this, uriSnap, mediaPath, title, ffmpeg);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                discoveredSubtitles = tracks;
                // Auto-enable first external subtitle if found alongside video
                SubtitleTrackInfo autoExt = tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ExternalFilePath));
                if (autoExt != null && string.IsNullOrEmpty(activeSubtitleTrackId)) {
                    SelectSubtitleTrack(autoExt);
                }
            });
        });
    }

    CavernizeTrack GetActiveTrack() => decodedTrueHDTrack ?? selectedTrack;

    bool IsSessionProtectedByQueue(string sessionDir) {
        if (string.IsNullOrEmpty(sessionDir)) {
            return false;
        }
        string full = Path.GetFullPath(sessionDir);
        return protectedQueuePaths.Any(p => p.StartsWith(full, StringComparison.OrdinalIgnoreCase));
    }

    IEnumerable<string> CollectActiveProtectedFiles(string alsoKeep = null) {
        List<string> list = [];
        if (!string.IsNullOrEmpty(alsoKeep)) {
            list.Add(alsoKeep);
        }
        if (LoadedFile != null && !string.IsNullOrEmpty(LoadedFile.Path)) {
            list.Add(LoadedFile.Path);
        }
        if (!string.IsNullOrEmpty(activeTrueHDSessionDir)) {
            list.Add(activeTrueHDSessionDir);
        }
        if (!string.IsNullOrEmpty(activeFallbackSessionDir)) {
            list.Add(activeFallbackSessionDir);
        }
        foreach (string p in protectedQueuePaths) {
            list.Add(p);
        }
        return list;
    }

    void ClearDecodedTrueHDState() {
        truehdPrepareGeneration++;
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        truehdPrepareCts = null;
        isPreparingTrueHD = false;

        ExternalDecodedTrack oldDecoded = decodedTrueHDTrack;
        decodedTrueHDTrack = null;
        string oldSession = activeTrueHDSessionDir;
        activeTrueHDSessionDir = null;

        if (oldDecoded != null && !protectedQueuePaths.Contains(oldDecoded.RawBitstreamPath ?? "") &&
            !IsSessionProtectedByQueue(oldSession)) {
            try {
                oldDecoded.Dispose();
            } catch {
                // Ignore
            }
        }
        if (!string.IsNullOrEmpty(oldSession) && !IsSessionProtectedByQueue(oldSession)) {
            TrueHDDecoderService.CleanupWorkDirectory(oldSession);
        }
    }

    public void Reset() {
        StopPlayback();
        ClearDecodedTrueHDState();
        environment.Reset();
        if (LoadedFile != null && renderQueue.Jobs.FirstOrDefault(x => x.IsUsingFile(LoadedFile)) == null) {
            LoadedFile.Dispose();
            LoadedFile = null;
        }
        if (!string.IsNullOrEmpty(activeFallbackSessionDir) && !IsSessionProtectedByQueue(activeFallbackSessionDir)) {
            TrueHDDecoderService.CleanupWorkDirectory(activeFallbackSessionDir);
            activeFallbackSessionDir = null;
        }
        MediaArtworkMetadata oldMeta = currentMediaMeta;
        currentMediaMeta = null;
        currentMediaUri = null;
        discoveredSubtitles = [];
        activeSubtitleTrackId = null;
        activeSubtitleCues = [];
        selectedTrack = null;
        SetFallbackTrackSourceItems(null);
        fileDetailsExpanded = false;
        SetWorkspaceHeaderText(S["SelectFilePill"]);
        SetWorkspaceSummaryText("");
        mediaNotification?.CancelNotification();
        RefreshMusicPlayerUi();
        _ = oldMeta;
        RebuildInlineTrackList();
        report = new PostRenderReport(environment.Listener);
    }

    void EnsureActiveQueueForCurrentFile(string targetPath) {
        if (string.IsNullOrWhiteSpace(targetPath)) {
            return;
        }
        int existingIdx = activeLibraryQueue.FindIndex(x => string.Equals(x.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0) {
            activeLibraryQueueIndex = existingIdx;
            return;
        }

        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot ?? MediaLibraryService.TryLoadCachedSnapshot(this);
        if (snap != null) {
            LibraryTrackItem match = snap.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) {
                // Check if current activeLibraryQueue already contains another format variant of this exact song
                if (activeLibraryQueue.Count > 0) {
                    List<LibraryTrackItem> matchVariants = MediaLibraryService.FindVariantsForTrack(
                        snap, targetPath, match.Title, match.EffectiveAlbumArtist, match.Album);
                    for (int i = 0; i < activeLibraryQueue.Count; i++) {
                        if (matchVariants.Any(v => string.Equals(v.FilePath, activeLibraryQueue[i].FilePath, StringComparison.OrdinalIgnoreCase))) {
                            activeLibraryQueue[i] = match;
                            activeLibraryQueueIndex = i;
                            return;
                        }
                    }
                }

                activeLibraryQueue.Clear();
                activeLibraryQueueIndex = -1;

                LibraryAlbumGroup alb = snap.Albums.FirstOrDefault(a => a.Tracks.Contains(match));
                List<LibraryTrackItem> group = alb != null && alb.Tracks.Count > 1
                    ? alb.Tracks
                    : snap.Tracks.Where(t => string.Equals(t.FolderPath, match.FolderPath, StringComparison.OrdinalIgnoreCase)).ToList();

                List<string> selectedFolders = store.GetLibraryFilterFolders();
                List<LibraryTrackItem> filteredGroup = group
                    .Where(t => MediaLibraryService.PassesAllFilters(
                        t, store.LibraryFilterMode, store.LibraryHideNonMusic, store.LibraryFolderFilterMode, selectedFolders))
                    .ToList();
                List<LibraryTrackItem> useList = filteredGroup.Contains(match) ? filteredGroup : group;
                List<LibraryMergedTrack> merged = MediaLibraryService.MergeAlbumTrackVariants(useList);
                foreach (LibraryMergedTrack m in merged) {
                    LibraryTrackItem pick = m.Variants.Contains(match)
                        ? match
                        : (preferredAudioCategory.HasValue
                            ? AudioQualityAnalyzer.PickBestVariantForNextTrack(m.Variants, preferredAudioCategory.Value, this)
                            : m.Primary);
                    activeLibraryQueue.Add(pick ?? m.Primary);
                }
                activeLibraryQueueIndex = activeLibraryQueue.IndexOf(match);
                if (activeLibraryQueue.Count > 0 && activeLibraryQueueIndex >= 0) {
                    return;
                }
                activeLibraryQueue.Clear();
            }
        }

        activeLibraryQueue.Clear();
        activeLibraryQueueIndex = -1;

        // Fallback: enumerate sibling media files in the same folder outside app cache
        try {
            string dir = Path.GetDirectoryName(targetPath);
            string cacheRoot = CacheDir?.AbsolutePath ?? "";
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                (string.IsNullOrEmpty(cacheRoot) || !dir.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))) {
                List<string> siblings = Directory.EnumerateFiles(dir)
                    .Where(f => MediaLibraryService.IsSupportedMediaExtension(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (string s in siblings) {
                    activeLibraryQueue.Add(new LibraryTrackItem {
                        FilePath = s,
                        Title = Path.GetFileNameWithoutExtension(s),
                        FolderPath = dir,
                        FolderName = Path.GetFileName(dir) ?? "",
                        FormatBadge = Path.GetExtension(s).TrimStart('.').ToUpperInvariant()
                    });
                }
                activeLibraryQueueIndex = activeLibraryQueue.FindIndex(x => string.Equals(x.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
            }
        } catch {
            // Keep queue empty on scoped folder error
        }
    }

    void OnSelectedTrackChanged() {
        StopPlayback();
        CancelNeuralTrackPrep(silent: true);
        ClearDecodedTrueHDState();
        if (selectedTrack == null) {
            fileDetailsExpanded = false;
            SetWorkspaceSummaryText("");
            SetFallbackTrackSourceItems(null);
            playerEngine?.BindSeparatedNeuralTrack(null);
            playerEngine?.VocalMixMatchStage?.ApplySongDna(Cavern.VocalChain.VocalMixDnaProfile.CreateStudioDefault());
            mediaNotification?.CancelNotification();
            RefreshPlayerSourcesList();
            RefreshMusicPlayerUi();
            RefreshExtractorChecklist();
            RefreshConverterTrackInfo();
            RebuildInlineTrackList();
            RefreshNeuralPrepUi();
            return;
        }

        bool shouldAutoPlay = pendingAutoPlayAfterLoad;
        pendingAutoPlayAfterLoad = false;

        if (selectedTrack.Codec == Codec.TrueHD) {
            AppLogger.Info($"Selected TrueHD track: {selectedTrack.FormatHeader}");
            SetFallbackTrackSourceItems(null);
            RebuildInlineTrackList();
            RefreshConverterTrackInfo();
            RefreshContainerOptions();
            RefreshMusicPlayerUi();
            if (!suppressAutoTrueHDPrepare) {
                PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: shouldAutoPlay);
            }
            return;
        }

        BindActiveTrackToUiAndPlayer(selectedTrack);
        if (shouldAutoPlay && selectedTrack.Supported && !(playerEngine?.IsPlaying ?? false)) {
            TogglePlayPause();
        }
    }

    void BindActiveTrackToUiAndPlayer(CavernizeTrack activeTrack) {
        if (activeTrack == null) {
            return;
        }
        string activeSourcePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        if (!string.Equals(activeSourcePath, lastRecordedTrackSourcePath, StringComparison.OrdinalIgnoreCase)) {
            StopStudioPerformanceRecording(autoSelectInExport: false);
            playerEngine?.SetPerformancePreviewTake(null, enabled: false);
            playerEngine?.LiveMicBridge?.ClearRecordedPerformance();
            if (store != null && store.StudioOptPerformance) {
                store.StudioOptPerformance = false;
                if (studioOptPerformanceSwitch != null && studioOptPerformanceSwitch.Checked) {
                    studioOptPerformanceSwitch.Checked = false;
                }
            }
            lastRecordedTrackSourcePath = activeSourcePath;
            RefreshStudioW12ContextUi();
        }
        AppLogger.Info($"Active track ready: {activeTrack.FormatHeader}");
        string details = string.Join(" | ", activeTrack.Details.Select(d => $"{d.property}: {d.value}"));
        SetWorkspaceSummaryText($"{activeTrack.FormatHeader}\n{details}");
        RebuildInlineTrackList();

        if (activeTrack.Supported && activeTrack.Codec != Codec.TrueHD) {
            try {
                RecordActiveTrackMeasuredLoudnessIfValid();
                playerEngine.Load(activeTrack, upmixingSettings, renderingSettings);
                PrimeEngineLoudnessForPath(activeSourcePath, currentMediaMeta);
                SetLiveTrackSourceItems();
            } catch (Exception ex) {
                AppLogger.Error("PlayerEngine.Load", ex);
                SetFallbackTrackSourceItems(TrackSourceItem.BuildFromTrack(activeTrack));
                ShowError(ex.Message);
            }
        } else {
            SetFallbackTrackSourceItems(TrackSourceItem.BuildFromTrack(activeTrack));
        }

        double initialSeekSec = 0;
        if (pendingSeekAfterLoadSeconds >= 0 && activeTrack.Supported && activeTrack.Codec != Codec.TrueHD && playerEngine.TotalSeconds > 0) {
            initialSeekSec = Math.Clamp(pendingSeekAfterLoadSeconds, 0, Math.Max(0, playerEngine.TotalSeconds - 0.1));
            pendingSeekAfterLoadSeconds = -1;
            playerEngine.Seek(initialSeekSec);
            musicVideoStage?.SeekTo(initialSeekSec);
        } else {
            pendingSeekAfterLoadSeconds = -1;
        }

        string metaTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : "Cavernize 3D");
        string metaArtist = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist)
            ? currentMediaMeta.Artist
            : $"Cavernize 3D • {activeTrack.FormatHeader}";
        string metaAlbum = !string.IsNullOrWhiteSpace(currentMediaMeta?.Album)
            ? currentMediaMeta.Album
            : activeTrack.FormatHeader;

        mediaNotification?.UpdateMetadata(
            metaTitle,
            metaArtist,
            metaAlbum,
            playerEngine.TotalSeconds,
            currentMediaMeta?.ArtworkBitmap);

        RefreshPlayerSourcesList();
        RefreshMusicPlayerUi();
        RefreshExtractorChecklist();
        RefreshConverterTrackInfo();
        RefreshContainerOptions();
        SyncPlaybackUi(false, initialSeekSec, playerEngine.TotalSeconds);
        UpdateRoomCanvas();
        playerEngine?.VocalMixMatchStage?.ApplySongDna(Cavern.VocalChain.VocalMixDnaProfile.CreateStudioDefault());
        // W51.5: Stagger heavy background scans so the first ~3s of playback have 100% uninterrupted RT CPU headroom
        StartLosslessTwinDiscoveryAsync(activeTrack, startupDelayMs: 1400);
        TryLoadCachedSongVocalDnaAsync(activeTrack, startupDelayMs: 300);
        TryAutoBindCachedNeuralStemsAsync(activeTrack, startupDelayMs: 450);
        ScheduleNextTrackLoudnessPreScan(activeSourcePath);
        RefreshNeuralPrepUi();
    }

    string lastLoudnessMeasuredPath = "";
    CancellationTokenSource nextTrackLoudnessCts;

    void RecordActiveTrackMeasuredLoudnessIfValid() {
        try {
            if (string.IsNullOrWhiteSpace(lastLoudnessMeasuredPath) || playerEngine == null) {
                return;
            }
            float integrated = playerEngine.LoudnessMeterStage.Snapshot.IntegratedLufs;
            if (float.IsFinite(integrated) && integrated >= -55f && integrated <= 3f) {
                AudioQualityAnalyzer.RecordMeasuredTrackIntegratedLufs(ApplicationContext, lastLoudnessMeasuredPath, integrated);
            }
        } catch {
            // Best-effort loudness caching
        }
    }

    void PrimeEngineLoudnessForPath(string trackPath, MediaArtworkMetadata meta) {
        lastLoudnessMeasuredPath = trackPath ?? "";
        if (playerEngine == null) {
            return;
        }
        if (meta?.TrackIntegratedLufs is float tagLufs && float.IsFinite(tagLufs)) {
            playerEngine.PrimeTrackLoudnessLufs(tagLufs);
            if (!string.IsNullOrEmpty(trackPath)) {
                AudioQualityAnalyzer.RecordMeasuredTrackIntegratedLufs(ApplicationContext, trackPath, tagLufs);
            }
            AppLogger.Info($"W51.6 Loudness primed from embedded tag: {tagLufs:F1} LUFS ({Path.GetFileName(trackPath ?? "")})");
            return;
        }
        if (!string.IsNullOrEmpty(trackPath) &&
            AudioQualityAnalyzer.TryGetCachedTrackIntegratedLufs(ApplicationContext, trackPath, out float cachedLufs)) {
            playerEngine.PrimeTrackLoudnessLufs(cachedLufs);
            AppLogger.Info($"W51.6 Loudness primed from cache: {cachedLufs:F1} LUFS ({Path.GetFileName(trackPath)})");
        }
    }

    void ScheduleNextTrackLoudnessPreScan(string currentTrackPath) {
        try { nextTrackLoudnessCts?.Cancel(); } catch { }
        CancellationTokenSource cts = new();
        nextTrackLoudnessCts = cts;
        CancellationToken ct = cts.Token;

        string nextQueuePath = null;
        if (activeLibraryQueue != null && activeLibraryQueueIndex >= 0 && activeLibraryQueueIndex + 1 < activeLibraryQueue.Count) {
            nextQueuePath = activeLibraryQueue[activeLibraryQueueIndex + 1]?.FilePath;
        }
        string ffmpegLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
        Context appCtx = ApplicationContext;

        _ = Task.Run(async () => {
            try {
                // 1. Pre-scan next track in album/queue first (eliminates loud next-track transition spikes!)
                if (!string.IsNullOrEmpty(nextQueuePath) && File.Exists(nextQueuePath)) {
                    float? nextLufs = await AudioQualityAnalyzer.PreScanTrackIntegratedLufsAsync(
                        appCtx, nextQueuePath, ffmpegLoc, startupDelayMs: 2600, ct).ConfigureAwait(false);
                    if (nextLufs.HasValue) {
                        AppLogger.Info($"W51.6 Pre-scanned next album track loudness: {nextLufs.Value:F1} LUFS ({Path.GetFileName(nextQueuePath)})");
                    }
                }
                // 2. Also pre-scan current track if uncached so future replays & cross-variant switches start at 0 dB error
                if (!string.IsNullOrEmpty(currentTrackPath) && File.Exists(currentTrackPath) &&
                    !AudioQualityAnalyzer.TryGetCachedTrackIntegratedLufs(appCtx, currentTrackPath, out _)) {
                    await AudioQualityAnalyzer.PreScanTrackIntegratedLufsAsync(
                        appCtx, currentTrackPath, ffmpegLoc, startupDelayMs: 3600, ct).ConfigureAwait(false);
                }
            } catch (System.OperationCanceledException) {
                // Superseded by track change
            } catch (ObjectDisposedException) {
                // CTS already disposed during teardown
            } catch {
                // Best-effort background pre-scan
            } finally {
                try { cts.Dispose(); } catch { }
            }
        });
    }

}
