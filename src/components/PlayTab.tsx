import React, { useEffect, useMemo, useState } from 'react';
import {
  Activity,
  Box,
  Disc3,
  FolderOpen,
  Mic,
  MicOff,
  Pause,
  Play,
  Repeat,
  RotateCcw,
  RotateCw,
  Search,
  SkipBack,
  SkipForward,
  Sliders,
  Sparkles,
  Volume2,
  X,
} from 'lucide-react';
import { HRIR_PROFILES } from '../data/catalog';
import type { StringKey } from '../i18n/strings';
import type { CavernSettings, SpatialTrack, TrackFormatVariant } from '../types/cavern';
import { formatTime } from '../utils/spatialMath';
import type {
  ScarlettHardwareControlState,
  VocalRackState,
} from '../utils/scarlettVocalDsp';
import { buildAudioSpecSummary, summarizeSingDock } from '../utils/w14AudioSpec';
import { ProceduralArtwork } from './ProceduralArtwork';

interface PlayTabProps {
  t: Record<StringKey, string>;
  tracks: SpatialTrack[];
  activeTrack: SpatialTrack;
  activeVariant: TrackFormatVariant;
  isPlaying: boolean;
  isLooping: boolean;
  currentTimeSec: number;
  masterVolume: number;
  settings: CavernSettings;
  onSelectTrack: (trackId: string) => void;
  onSelectVariant: (variantId: string) => void;
  onTogglePlay: () => void;
  onSeek: (timeSec: number) => void;
  onSkipRelative: (deltaSec: number) => void;
  onPrevTrack: () => void;
  onNextTrack: () => void;
  onToggleLoop: () => void;
  onVolumeChange: (vol: number) => void;
  onUpdateSettings: (patch: Partial<CavernSettings>) => void;
  onOpenRoomTab: () => void;
  onUploadLocalFile: (file: File) => void;
  scarlett: ScarlettHardwareControlState;
  vocalRack: VocalRackState;
  lgsrBypassed: boolean;
  onToggleLgsrBypass: () => void;
  onUpdateVocalRack: (patch: Partial<VocalRackState>) => void;
  onOpenScarlettConsole: () => void;
}

export const PlayTab: React.FC<PlayTabProps> = ({
  t,
  tracks,
  activeTrack,
  activeVariant,
  isPlaying,
  isLooping,
  currentTimeSec,
  masterVolume,
  settings,
  onSelectTrack,
  onSelectVariant,
  onTogglePlay,
  onSeek,
  onSkipRelative,
  onPrevTrack,
  onNextTrack,
  onToggleLoop,
  onVolumeChange,
  onUpdateSettings,
  onOpenRoomTab,
  onUploadLocalFile,
  scarlett,
  vocalRack,
  lgsrBypassed,
  onToggleLgsrBypass,
  onUpdateVocalRack,
  onOpenScarlettConsole,
}) => {
  const [subView, setSubView] = useState<'now-playing' | 'library'>('now-playing');
  const [stageMode, setStageMode] = useState<'cover' | 'lyrics'>('cover');
  const [searchQuery, setSearchQuery] = useState('');
  const [libraryFilter, setLibraryFilter] = useState<'all' | 'spatial'>('all');
  const [smartClean, setSmartClean] = useState(true);
  const [audioSpecOpen, setAudioSpecOpen] = useState(false);
  const [showSingMixMatch, setShowSingMixMatch] = useState(false);

  useEffect(() => {
    if (!audioSpecOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setAudioSpecOpen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [audioSpecOpen]);

  const activeProfile = useMemo(
    () => HRIR_PROFILES.find((p) => p.id === settings.hrirProfileId) ?? HRIR_PROFILES[0],
    [settings.hrirProfileId],
  );

  const audioSpec = useMemo(
    () => buildAudioSpecSummary(activeTrack, activeVariant, settings, lgsrBypassed),
    [activeTrack, activeVariant, settings, lgsrBypassed],
  );

  const singSummary = useMemo(
    () => summarizeSingDock(vocalRack, settings.liveMicEnabled),
    [vocalRack, settings.liveMicEnabled],
  );

  const activeLyricIdx = useMemo(() => {
    if (activeTrack.lyrics.length === 0) return -1;
    let idx = 0;
    for (let i = 0; i < activeTrack.lyrics.length; i += 1) {
      if (currentTimeSec >= activeTrack.lyrics[i].timeSec) {
        idx = i;
      }
    }
    return idx;
  }, [activeTrack.lyrics, currentTimeSec]);

  const filteredTracks = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    return tracks.filter((tr) => {
      if (smartClean && tr.isNonMusicNoise && !tr.isSpatial) return false;
      if (libraryFilter === 'spatial' && !tr.isSpatial) return false;
      if (!q) return true;
      return (
        tr.title.toLowerCase().includes(q) ||
        tr.artist.toLowerCase().includes(q) ||
        tr.album.toLowerCase().includes(q) ||
        tr.folder.toLowerCase().includes(q)
      );
    });
  }, [tracks, searchQuery, libraryFilter, smartClean]);

  const hiddenNoiseCount = useMemo(
    () => (smartClean ? tracks.filter((tr) => tr.isNonMusicNoise && !tr.isSpatial).length : 0),
    [tracks, smartClean],
  );

  const waveformBars = useMemo(() => {
    const seed = activeTrack.waveformSeed;
    return Array.from({ length: 48 }, (_, i) => {
      const h = 24 + Math.abs(Math.sin((i + seed) * 0.43) * 52 + Math.cos(i * 0.19) * 20);
      return Math.round(Math.min(100, h));
    });
  }, [activeTrack.waveformSeed]);

  const progressRatio = activeTrack.durationSec > 0 ? currentTimeSec / activeTrack.durationSec : 0;

  return (
    <section aria-label={t.tabPlay} className="space-y-6">
      {/* Sub-navigation bar: Now Playing | Library + Open Audio File */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div
          role="tablist"
          aria-label="Player view"
          className="inline-flex items-center gap-1 p-1 rounded-xl bg-[#15161C] border border-white/10"
        >
          <button
            type="button"
            role="tab"
            aria-selected={subView === 'now-playing'}
            onClick={() => setSubView('now-playing')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-colors whitespace-nowrap shrink-0 ${
              subView === 'now-playing'
                ? 'bg-[#387AFF] text-white'
                : 'text-[#B7B7BB] hover:text-white'
            }`}
          >
            {t.nowPlaying}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={subView === 'library'}
            onClick={() => setSubView('library')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-colors whitespace-nowrap shrink-0 ${
              subView === 'library'
                ? 'bg-[#387AFF] text-white'
                : 'text-[#B7B7BB] hover:text-white'
            }`}
          >
            {t.library} ({filteredTracks.length})
          </button>
        </div>

        <label className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg bg-[#1E1F27] hover:bg-[#272936] text-[#FCFCFF] border border-white/10 cursor-pointer transition-colors whitespace-nowrap shrink-0">
          <FolderOpen className="w-4 h-4 text-[#6E9CFF]" />
          <span>{t.openFile}</span>
          <input
            type="file"
            accept="audio/*,.wav,.flac,.mp3,.m4a,.ogg,.webm,.mp4"
            className="sr-only"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) {
                onUploadLocalFile(file);
                setSubView('now-playing');
              }
              e.target.value = '';
            }}
          />
        </label>
      </div>

      {subView === 'now-playing' ? (
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
          {/* Left column: Artwork / Live Lyrics Stage */}
          <div className="lg:col-span-5 bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
            <div className="flex items-center justify-between gap-2">
              <div className="inline-flex items-center gap-1 p-1 rounded-lg bg-[#0C0E14] border border-white/8">
                <button
                  type="button"
                  onClick={() => setStageMode('cover')}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 ${
                    stageMode === 'cover'
                      ? 'bg-white/12 text-white'
                      : 'text-[#99999E] hover:text-white'
                  }`}
                >
                  {t.modeCover}
                </button>
                <button
                  type="button"
                  onClick={() => setStageMode('lyrics')}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 ${
                    stageMode === 'lyrics'
                      ? 'bg-white/12 text-white'
                      : 'text-[#99999E] hover:text-white'
                  }`}
                >
                  {t.modeLyrics}
                </button>
              </div>
              <span className="text-xs text-[#99999E] font-mono tabular-nums truncate">
                {activeVariant.channels}
              </span>
            </div>

            {stageMode === 'cover' ? (
              <ProceduralArtwork track={activeTrack} isPlaying={isPlaying} size="lg" />
            ) : (
              <div className="aspect-square rounded-2xl bg-[#0C0E14] border border-white/8 p-5 flex flex-col justify-between overflow-hidden">
                <p className="text-xs text-[#99999E]">{t.lyricsHint}</p>
                {activeTrack.lyrics.length === 0 ? (
                  <p className="text-sm text-[#99999E] my-auto text-center px-4">
                    {t.noLyrics}
                  </p>
                ) : (
                  <div className="space-y-2.5 overflow-y-auto pr-1 my-3 flex-1">
                    {activeTrack.lyrics.map((line, idx) => {
                      const isActive = idx === activeLyricIdx;
                      return (
                        <button
                          key={`${line.timeSec}-${idx}`}
                          type="button"
                          onClick={() => onSeek(line.timeSec)}
                          className={`w-full text-left px-3 py-2 rounded-lg transition-colors text-sm leading-relaxed ${
                            isActive
                              ? 'bg-[#387AFF]/20 text-white font-semibold'
                              : 'text-[#99999E] hover:text-[#E9E9EC] hover:bg-white/5'
                          }`}
                        >
                          <span className="font-mono tabular-nums text-xs text-[#6E9CFF] mr-2">
                            {formatTime(line.timeSec)}
                          </span>
                          {line.text}
                        </button>
                      );
                    })}
                  </div>
                )}
                {/* Apple Music Sing Vocal Level + Auto-Vocal Mix-Match & Scarlett Dock */}
                <div className="pt-3 mt-2 border-t border-white/8 space-y-2.5">
                  <div className="flex flex-wrap items-center justify-between gap-2 text-xs">
                    <div className="flex items-center gap-1.5 min-w-0">
                      {singSummary.isMicLive ? (
                        <Mic className="w-3.5 h-3.5 text-[#56B8A8] shrink-0" />
                      ) : (
                        <MicOff className="w-3.5 h-3.5 text-[#89B0FF] shrink-0" />
                      )}
                      <span className="text-[#E9E9EC] font-medium truncate">
                        {t.karaokeVocalLevel}
                      </span>
                      <span className="font-mono tabular-nums text-white font-semibold">
                        {singSummary.vocalLevelPct}%
                      </span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <button
                        type="button"
                        aria-expanded={showSingMixMatch}
                        onClick={() => setShowSingMixMatch((v) => !v)}
                        className={`inline-flex items-center gap-1 px-2 py-1 text-[11px] font-semibold rounded-md border transition-colors whitespace-nowrap ${
                          showSingMixMatch || singSummary.isMicLive
                            ? 'bg-[#387AFF]/20 border-[#598FFF]/50 text-[#C2D8FF]'
                            : 'bg-white/6 border-white/10 text-[#B7B7BB] hover:text-white'
                        }`}
                      >
                        <Sliders className="w-3 h-3" />
                        <span>{t.singMixMatchToggle}</span>
                      </button>
                      <button
                        type="button"
                        onClick={onOpenScarlettConsole}
                        className="px-2 py-1 text-[11px] font-semibold rounded-md bg-[#DC2626]/20 hover:bg-[#DC2626]/35 text-[#FCA5A5] border border-[#DC2626]/40 whitespace-nowrap"
                      >
                        {t.openScarlettConsole} · {scarlett.channels[0].airMode.toUpperCase()}
                      </button>
                    </div>
                  </div>

                  <input
                    type="range"
                    aria-label={t.karaokeVocalLevel}
                    min={0}
                    max={100}
                    value={vocalRack.karaokeBackingVocalPct}
                    onChange={(e) =>
                      onUpdateVocalRack({ karaokeBackingVocalPct: Number(e.target.value) })
                    }
                    className="cavern-slider w-full"
                  />

                  {showSingMixMatch && (
                    <div className="p-2.5 rounded-xl bg-[#15161C] border border-white/10 space-y-2">
                      <div className="flex flex-wrap items-center justify-between gap-1.5">
                        <button
                          type="button"
                          aria-pressed={settings.liveMicEnabled}
                          onClick={() =>
                            onUpdateSettings({ liveMicEnabled: !settings.liveMicEnabled })
                          }
                          className={`px-2 py-1 text-[11px] font-semibold rounded-md border transition-colors whitespace-nowrap ${
                            settings.liveMicEnabled
                              ? 'bg-[#56B8A8]/20 border-[#56B8A8]/50 text-[#88E0D1]'
                              : 'bg-white/5 border-white/10 text-[#99999E] hover:text-white'
                          }`}
                        >
                          {settings.liveMicEnabled ? t.singMicLiveLabel : t.singMicOffLabel}
                        </button>
                        <button
                          type="button"
                          aria-pressed={vocalRack.bypass}
                          onClick={() => onUpdateVocalRack({ bypass: !vocalRack.bypass })}
                          className={`px-2 py-1 text-[11px] font-semibold rounded-md border transition-colors whitespace-nowrap ${
                            vocalRack.bypass
                              ? 'bg-[#F59E0B]/20 border-[#F59E0B]/45 text-[#FCD34D]'
                              : 'bg-[#387AFF]/18 border-[#598FFF]/40 text-[#89B0FF]'
                          }`}
                        >
                          {vocalRack.bypass ? t.singVocalBypassLabel : t.singVocalWetLabel}
                        </button>
                      </div>
                      <div className="grid grid-cols-2 gap-2.5">
                        <div>
                          <div className="flex justify-between text-[10px] text-[#99999E] mb-0.5">
                            <span className="truncate">{t.vocalMacroMixMatch}</span>
                            <span className="font-mono tabular-nums text-white">
                              {singSummary.mixMatchPct}%
                            </span>
                          </div>
                          <input
                            type="range"
                            aria-label={t.vocalMacroMixMatch}
                            min={0}
                            max={100}
                            value={vocalRack.mixMatchPct}
                            onChange={(e) =>
                              onUpdateVocalRack({ mixMatchPct: Number(e.target.value) })
                            }
                            className="cavern-slider w-full"
                          />
                        </div>
                        <div>
                          <div className="flex justify-between text-[10px] text-[#99999E] mb-0.5">
                            <span className="truncate">{t.vocalMacroReverb}</span>
                            <span className="font-mono tabular-nums text-white">
                              {singSummary.reverbPct}%
                            </span>
                          </div>
                          <input
                            type="range"
                            aria-label={t.vocalMacroReverb}
                            min={0}
                            max={100}
                            value={vocalRack.reverbPct}
                            onChange={(e) =>
                              onUpdateVocalRack({ reverbPct: Number(e.target.value) })
                            }
                            className="cavern-slider w-full"
                          />
                        </div>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>

          {/* Right column: Metadata, Format Switcher, Spatial Toggles, Wave Seekbar & Transport */}
          <div className="lg:col-span-7 bg-[#15161C] border border-white/10 rounded-2xl p-6 space-y-6">
            {/* Track title & unboxed metadata */}
            <div className="space-y-2">
              <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
                <div className="min-w-0">
                  <h2 className="text-xl sm:text-2xl font-bold tracking-tight text-white leading-snug">
                    {activeTrack.title}
                  </h2>
                  <p className="text-sm text-[#B7B7BB] mt-1">
                    {activeTrack.artist} · {activeTrack.album}
                  </p>
                </div>
                <div className="flex flex-wrap items-center gap-2 shrink-0">
                  <button
                    type="button"
                    data-testid="audio-spec-badge"
                    onClick={() => setAudioSpecOpen(true)}
                    className={`inline-flex items-center gap-1.5 px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                      audioSpec.lgsr.badgeMode === 'atmos-flac'
                        ? 'bg-[#56B8A8]/18 border-[#56B8A8]/55 text-[#88E0D1] hover:bg-[#56B8A8]/28'
                        : audioSpec.lgsr.badgeMode === 'atmos-ab-bypass'
                          ? 'bg-[#F59E0B]/18 border-[#F59E0B]/50 text-[#FCD34D] hover:bg-[#F59E0B]/28'
                          : 'bg-[#1E1F27] border-white/12 text-[#E9E9EC] hover:bg-[#282A36]'
                    }`}
                  >
                    <Activity className="w-3.5 h-3.5" />
                    <span>
                      {audioSpec.lgsr.badgeMode === 'atmos-flac'
                        ? t.audioSpecBadgeAtmosFlac
                        : audioSpec.lgsr.badgeMode === 'atmos-ab-bypass'
                          ? t.audioSpecBadgeAtmosAb
                          : t.audioSpecBtn}
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={onOpenRoomTab}
                    className="inline-flex items-center gap-2 px-3.5 py-2 text-xs font-semibold rounded-lg bg-[#387AFF]/15 text-[#89B0FF] hover:bg-[#387AFF]/25 border border-[#387AFF]/30 transition-colors whitespace-nowrap shrink-0"
                  >
                    <Box className="w-3.5 h-3.5" />
                    <span>{t.openInRoom}</span>
                  </button>
                </div>
              </div>

              <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-[#99999E] font-mono tabular-nums pt-1">
                <span>{activeVariant.codec}</span>
                <span aria-hidden="true">·</span>
                <span>{(activeVariant.sampleRateHz / 1000).toFixed(1)} kHz</span>
                <span aria-hidden="true">·</span>
                <span>{activeVariant.bitDepth}-bit</span>
                <span aria-hidden="true">·</span>
                <span>{activeVariant.bitrateKbps} kbps</span>
                {activeVariant.objectCount > 0 && (
                  <>
                    <span aria-hidden="true">·</span>
                    <span className="text-[#F56962]">
                      {t.dynamicObjectsLabel.replace('{count}', String(activeVariant.objectCount))}
                    </span>
                  </>
                )}
              </div>
            </div>

            {/* Live Multi-Format Switcher (preserves exact playback timestamp) */}
            {activeTrack.variants.length > 1 && (
              <div className="flex flex-wrap items-center gap-2 pt-1 border-t border-white/8">
                <span className="text-xs font-medium text-[#99999E] mr-1">
                  {t.formatLabel}:
                </span>
                {activeTrack.variants.map((variant) => {
                  const selected = variant.id === activeVariant.id;
                  return (
                    <button
                      key={variant.id}
                      type="button"
                      onClick={() => onSelectVariant(variant.id)}
                      className={`px-3 py-1.5 text-xs font-medium rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                        selected
                          ? 'bg-[#387AFF] border-[#598FFF] text-white'
                          : 'bg-[#1E1F27] border-white/10 text-[#B7B7BB] hover:text-white'
                      }`}
                    >
                      {variant.label}
                    </button>
                  );
                })}
              </div>
            )}

            {/* Quick Spatial DSP Toggles (matching CavernizeAndroid flanking bar) */}
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
              <button
                type="button"
                aria-pressed={settings.cavernize3D}
                onClick={() => onUpdateSettings({ cavernize3D: !settings.cavernize3D })}
                className={`px-3.5 py-3 rounded-xl border text-left transition-colors ${
                  settings.cavernize3D
                    ? 'bg-[#387AFF]/18 border-[#598FFF]/50 text-white'
                    : 'bg-[#1E1F27] border-white/8 text-[#B7B7BB] hover:text-white'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className="text-xs font-semibold">{t.cavernize3D}</span>
                  <Sparkles className="w-3.5 h-3.5 text-[#8E96FA]" />
                </div>
                <p className="text-[11px] text-[#99999E] mt-1">
                  {settings.cavernize3D ? t.cavernize3DActive : t.cavernize3DBypassed}
                </p>
              </button>

              <button
                type="button"
                aria-pressed={settings.headphoneVirtualizer && settings.hrirProfileId !== 'direct_2_0'}
                onClick={() =>
                  onUpdateSettings({
                    headphoneVirtualizer: !settings.headphoneVirtualizer,
                    hrirProfileId:
                      settings.hrirProfileId === 'direct_2_0'
                        ? 'sadie_d1_dfc'
                        : settings.hrirProfileId,
                  })
                }
                className={`px-3.5 py-3 rounded-xl border text-left transition-colors ${
                  settings.headphoneVirtualizer && settings.hrirProfileId !== 'direct_2_0'
                    ? 'bg-[#387AFF]/18 border-[#598FFF]/50 text-white'
                    : 'bg-[#1E1F27] border-white/8 text-[#B7B7BB] hover:text-white'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className="text-xs font-semibold">
                    {settings.headphoneVirtualizer && settings.hrirProfileId !== 'direct_2_0'
                      ? t.binauralProfile
                      : t.directStereo}
                  </span>
                  <Disc3 className="w-3.5 h-3.5 text-[#598FFF]" />
                </div>
                <p className="text-[11px] text-[#99999E] mt-1 truncate">
                  {settings.headphoneVirtualizer ? activeProfile.dataset : t.noHrtf}
                </p>
              </button>

              <button
                type="button"
                aria-pressed={settings.matrixUpmix}
                onClick={() => onUpdateSettings({ matrixUpmix: !settings.matrixUpmix })}
                className={`px-3.5 py-3 rounded-xl border text-left transition-colors ${
                  settings.matrixUpmix
                    ? 'bg-[#387AFF]/18 border-[#598FFF]/50 text-white'
                    : 'bg-[#1E1F27] border-white/8 text-[#B7B7BB] hover:text-white'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className="text-xs font-semibold">{t.matrix71}</span>
                  <span className="text-[11px] font-mono text-[#56B8A8]">
                    {settings.matrixUpmix ? t.onLabel : t.offLabel}
                  </span>
                </div>
                <p className="text-[11px] text-[#99999E] mt-1">
                  {settings.matrixUpmix ? t.matrix71Active : t.matrix71Bypassed}
                </p>
              </button>
            </div>

            {/* Interactive Wave Seekbar */}
            <div className="space-y-2 pt-2">
              <div
                className="relative h-12 flex items-end gap-1 cursor-pointer select-none px-1"
                onClick={(e) => {
                  const rect = e.currentTarget.getBoundingClientRect();
                  const ratio = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
                  onSeek(ratio * activeTrack.durationSec);
                }}
              >
                {waveformBars.map((heightPct, idx) => {
                  const barRatio = idx / waveformBars.length;
                  const played = barRatio <= progressRatio;
                  return (
                    <div
                      key={idx}
                      className={`flex-1 rounded-full transition-colors ${
                        played ? 'bg-[#598FFF]' : 'bg-white/15 hover:bg-white/25'
                      }`}
                      style={{ height: `${heightPct}%` }}
                    />
                  );
                })}
              </div>

              <div className="flex items-center justify-between text-xs font-mono tabular-nums text-[#99999E]">
                <span>{formatTime(currentTimeSec)}</span>
                <input
                  type="range"
                  aria-label="Seek playback position"
                  min={0}
                  max={Math.max(1, activeTrack.durationSec)}
                  step={0.5}
                  value={Math.min(currentTimeSec, activeTrack.durationSec)}
                  onChange={(e) => onSeek(Number(e.target.value))}
                  className="cavern-slider mx-4 flex-1"
                />
                <span>{formatTime(activeTrack.durationSec)}</span>
              </div>
            </div>

            {/* Unified Frosted Transport Bar */}
            <div className="flex flex-wrap items-center justify-between gap-4 pt-2 border-t border-white/8">
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  aria-label="Previous track"
                  onClick={onPrevTrack}
                  className="w-10 h-10 rounded-full flex items-center justify-center bg-[#1E1F27] hover:bg-[#2B2D3B] text-[#E9E9EC] border border-white/10 transition-colors"
                >
                  <SkipBack className="w-4 h-4" />
                </button>
                <button
                  type="button"
                  aria-label="Rewind 10 seconds"
                  onClick={() => onSkipRelative(-10)}
                  className="w-10 h-10 rounded-full flex items-center justify-center bg-[#1E1F27] hover:bg-[#2B2D3B] text-[#E9E9EC] border border-white/10 transition-colors"
                >
                  <RotateCcw className="w-4 h-4" />
                </button>
                <button
                  type="button"
                  aria-label={isPlaying ? 'Pause playback' : 'Play spatial audio'}
                  onClick={onTogglePlay}
                  className="w-12 h-12 rounded-full flex items-center justify-center bg-[#387AFF] hover:bg-[#598FFF] text-white transition-colors"
                >
                  {isPlaying ? <Pause className="w-5 h-5" /> : <Play className="w-5 h-5 ml-0.5" />}
                </button>
                <button
                  type="button"
                  aria-label="Forward 10 seconds"
                  onClick={() => onSkipRelative(10)}
                  className="w-10 h-10 rounded-full flex items-center justify-center bg-[#1E1F27] hover:bg-[#2B2D3B] text-[#E9E9EC] border border-white/10 transition-colors"
                >
                  <RotateCw className="w-4 h-4" />
                </button>
                <button
                  type="button"
                  aria-label="Next track"
                  onClick={onNextTrack}
                  className="w-10 h-10 rounded-full flex items-center justify-center bg-[#1E1F27] hover:bg-[#2B2D3B] text-[#E9E9EC] border border-white/10 transition-colors"
                >
                  <SkipForward className="w-4 h-4" />
                </button>
                <button
                  type="button"
                  aria-label="Toggle loop"
                  aria-pressed={isLooping}
                  onClick={onToggleLoop}
                  className={`w-10 h-10 rounded-full flex items-center justify-center border transition-colors ${
                    isLooping
                      ? 'bg-[#387AFF]/20 border-[#598FFF]/50 text-[#89B0FF]'
                      : 'bg-[#1E1F27] border-white/10 text-[#99999E] hover:text-white'
                  }`}
                >
                  <Repeat className="w-4 h-4" />
                </button>
              </div>

              {/* Volume control */}
              <div className="flex items-center gap-3 min-w-[180px] flex-1 max-w-xs">
                <Volume2 className="w-4 h-4 text-[#99999E] shrink-0" />
                <input
                  type="range"
                  aria-label={t.masterVolume}
                  min={0}
                  max={1}
                  step={0.01}
                  value={masterVolume}
                  onChange={(e) => onVolumeChange(Number(e.target.value))}
                  className="cavern-slider w-full"
                />
                <span className="text-xs font-mono tabular-nums text-[#B7B7BB] w-9 text-right">
                  {Math.round(masterVolume * 100)}%
                </span>
              </div>
            </div>
          </div>
        </div>
      ) : (
        /* In-App Media Library View */
        <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="relative flex-1 min-w-[240px]">
              <Search className="w-4 h-4 text-[#99999E] absolute left-3.5 top-1/2 -translate-y-1/2 pointer-events-none" />
              <input
                type="search"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder={t.searchLibrary}
                aria-label={t.searchLibrary}
                className="w-full pl-10 pr-4 py-2.5 text-sm rounded-xl bg-[#0C0E14] border border-white/10 text-white placeholder:text-[#99999E] focus:outline-none focus:border-[#598FFF]"
              />
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <div className="inline-flex items-center gap-1 p-1 rounded-lg bg-[#0C0E14] border border-white/8">
                <button
                  type="button"
                  onClick={() => setLibraryFilter('all')}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 ${
                    libraryFilter === 'all'
                      ? 'bg-[#387AFF] text-white'
                      : 'text-[#B7B7BB] hover:text-white'
                  }`}
                >
                  {t.filterAll}
                </button>
                <button
                  type="button"
                  onClick={() => setLibraryFilter('spatial')}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 ${
                    libraryFilter === 'spatial'
                      ? 'bg-[#387AFF] text-white'
                      : 'text-[#B7B7BB] hover:text-white'
                  }`}
                >
                  {t.filterSpatial}
                </button>
              </div>

              <button
                type="button"
                aria-pressed={smartClean}
                onClick={() => setSmartClean((v) => !v)}
                className={`px-3 py-2 text-xs font-medium rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                  smartClean
                    ? 'bg-[#56B8A8]/18 border-[#56B8A8]/45 text-[#88E0D1]'
                    : 'bg-[#1E1F27] border-white/10 text-[#99999E] hover:text-white'
                }`}
              >
                {t.smartClean}
                {hiddenNoiseCount > 0 ? ` (-${hiddenNoiseCount})` : ''}
              </button>
            </div>
          </div>

          {filteredTracks.length === 0 ? (
            <div className="py-12 text-center text-sm text-[#99999E]">
              {t.noTracksMatched}
            </div>
          ) : (
            <div className="divide-y divide-white/8">
              {filteredTracks.map((track) => {
                const isSelected = track.id === activeTrack.id;
                const primaryFmt = track.variants[0];
                return (
                  <button
                    key={track.id}
                    type="button"
                    onClick={() => {
                      onSelectTrack(track.id);
                      setSubView('now-playing');
                    }}
                    className={`w-full flex items-center justify-between gap-4 py-3 px-3 rounded-xl text-left transition-colors ${
                      isSelected ? 'bg-[#387AFF]/15' : 'hover:bg-white/[0.04]'
                    }`}
                  >
                    <div className="flex items-center gap-3.5 min-w-0">
                      <ProceduralArtwork track={track} isPlaying={isSelected && isPlaying} size="sm" />
                      <div className="min-w-0">
                        <p className={`text-sm font-semibold truncate ${isSelected ? 'text-[#89B0FF]' : 'text-white'}`}>
                          {track.title}
                        </p>
                        <p className="text-xs text-[#99999E] truncate mt-0.5">
                          {track.artist} · {track.album} · {track.folder}
                        </p>
                      </div>
                    </div>

                    <div className="flex items-center gap-4 shrink-0 text-xs font-mono tabular-nums">
                      <span className="hidden sm:inline text-[#B7B7BB]">
                        {track.variants.map((v) => v.label).join(' + ')}
                      </span>
                      <span className="text-[#99999E]">
                        {primaryFmt.channels}
                      </span>
                      <span className="text-[#B7B7BB] w-10 text-right">
                        {formatTime(track.durationSec)}
                      </span>
                    </div>
                  </button>
                );
              })}
            </div>
          )}
        </div>
      )}
      {/* W14 Audio Specification & ATMOS+FLAC Instant A/B Compare Modal */}
      {audioSpecOpen && (
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="audio-spec-modal-title"
          className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm"
          onClick={() => setAudioSpecOpen(false)}
        >
          <div
            className="w-full max-w-lg rounded-2xl bg-[#15161C] border border-white/14 shadow-2xl p-5 sm:p-6 space-y-5"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-start justify-between gap-4">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <h3
                    id="audio-spec-modal-title"
                    className="text-base font-bold text-white leading-snug"
                  >
                    {t.audioSpecTitle}
                  </h3>
                  {audioSpec.lgsr.badgeMode !== 'native' && (
                    <span
                      className={`px-2 py-0.5 text-[11px] font-mono font-semibold rounded-md border ${
                        audioSpec.lgsr.badgeMode === 'atmos-flac'
                          ? 'bg-[#56B8A8]/20 border-[#56B8A8]/50 text-[#88E0D1]'
                          : 'bg-[#F59E0B]/20 border-[#F59E0B]/50 text-[#FCD34D]'
                      }`}
                    >
                      {audioSpec.lgsr.badgeMode === 'atmos-flac'
                        ? t.audioSpecBadgeAtmosFlac
                        : t.audioSpecBadgeAtmosAb}
                    </span>
                  )}
                </div>
                <p className="text-xs text-[#99999E] mt-1 truncate">
                  {activeTrack.title} · {activeVariant.codec}
                </p>
              </div>
              <button
                type="button"
                aria-label={t.audioSpecClose}
                onClick={() => setAudioSpecOpen(false)}
                className="w-8 h-8 rounded-lg flex items-center justify-center bg-white/6 hover:bg-white/12 text-[#B7B7BB] hover:text-white shrink-0"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="grid grid-cols-2 gap-3 text-xs">
              <div className="p-3 rounded-xl bg-[#0C0E14] border border-white/8">
                <p className="text-[#99999E]">{t.audioSpecQualityTier}</p>
                <p className="font-semibold text-white mt-1">{audioSpec.qualityTier}</p>
              </div>
              <div className="p-3 rounded-xl bg-[#0C0E14] border border-white/8">
                <p className="text-[#99999E]">{t.audioSpecDynamicRange}</p>
                <p className="font-mono tabular-nums font-semibold text-white mt-1">
                  DR {audioSpec.effectiveDynamicRangeDb.toFixed(1)} dB
                </p>
              </div>
              <div className="p-3 rounded-xl bg-[#0C0E14] border border-white/8">
                <p className="text-[#99999E]">{t.audioSpecBandwidth}</p>
                <p className="font-mono tabular-nums font-semibold text-white mt-1">
                  0 – {audioSpec.nyquistKhz.toFixed(1)} kHz ({(activeVariant.sampleRateHz / 1000).toFixed(1)} kHz)
                </p>
              </div>
              <div className="p-3 rounded-xl bg-[#0C0E14] border border-white/8">
                <p className="text-[#99999E]">{t.audioSpecBitIntegrity}</p>
                <p className="font-semibold text-white mt-1">{audioSpec.bitDepthIntegrity}</p>
              </div>
            </div>

            <div className="p-4 rounded-xl bg-[#1C1E27] border border-white/10 space-y-3">
              <div className="flex items-center justify-between gap-2">
                <span className="text-xs font-semibold text-white">{t.audioSpecLgsrHeading}</span>
                {audioSpec.lgsr.twinVariant && (
                  <span className="text-[11px] font-mono tabular-nums text-[#88E0D1]">
                    GCC {audioSpec.lgsr.gccOffsetMs >= 0 ? '+' : ''}
                    {audioSpec.lgsr.gccOffsetMs.toFixed(3)} ms · {Math.round(audioSpec.lgsr.coherenceScore * 100)}%
                  </span>
                )}
              </div>

              {audioSpec.lgsr.twinVariant && settings.lgsrEnabled ? (
                <>
                  <div className="text-xs text-[#B7B7BB] space-y-1">
                    <p>
                      <span className="text-[#99999E]">{t.audioSpecLgsrPairedWith}: </span>
                      <span className="font-mono text-white">
                        {audioSpec.lgsr.twinVariant.label} ({audioSpec.lgsr.twinVariant.codec})
                      </span>
                    </p>
                    <p
                      data-testid="audio-spec-ab-hint"
                      className={audioSpec.lgsr.isBypassedForAb ? 'text-[#FCD34D]' : 'text-[#88E0D1]'}
                    >
                      {audioSpec.lgsr.isBypassedForAb
                        ? t.audioSpecLgsrOriginalHint
                        : t.audioSpecLgsrRestoredHint}
                    </p>
                  </div>
                  <button
                    type="button"
                    data-testid="audio-spec-ab-toggle"
                    onClick={onToggleLgsrBypass}
                    className={`w-full py-2.5 px-4 text-xs font-semibold rounded-xl border transition-colors ${
                      audioSpec.lgsr.isBypassedForAb
                        ? 'bg-[#56B8A8]/20 hover:bg-[#56B8A8]/30 border-[#56B8A8]/55 text-[#88E0D1]'
                        : 'bg-[#F59E0B]/18 hover:bg-[#F59E0B]/28 border-[#F59E0B]/50 text-[#FCD34D]'
                    }`}
                  >
                    {audioSpec.lgsr.isBypassedForAb
                      ? t.audioSpecLgsrAbToRestored
                      : t.audioSpecLgsrAbToOriginal}
                  </button>
                </>
              ) : (
                <p className="text-xs text-[#99999E]">{t.audioSpecLgsrNotPaired}</p>
              )}
            </div>
          </div>
        </div>
      )}
    </section>
  );
};
