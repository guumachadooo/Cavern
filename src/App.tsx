import { useEffect, useMemo, useRef, useState } from 'react';
import { Pause, Play } from 'lucide-react';
import { CavernSpatialAudioEngine } from './audio/spatialEngine';
import { PlayTab } from './components/PlayTab';
import { RoomTab } from './components/RoomTab';
import { SettingsTab } from './components/SettingsTab';
import { StemsTab } from './components/StemsTab';
import { StudioTab } from './components/StudioTab';
import { createDefaultSpatialSources, DEMO_TRACKS } from './data/catalog';
import { getStrings } from './i18n/strings';
import type {
  CavernSettings,
  SpatialSourceNode,
  SpatialTrack,
  TabId,
  Vec3,
} from './types/cavern';
import { clamp, evaluateSourcePosition, formatTime } from './utils/spatialMath';

const DEFAULT_SETTINGS: CavernSettings = {
  locale: 'en-US',
  hrirProfileId: 'sadie_d1_dfc',
  customHrirName: null,
  headphoneVirtualizer: true,
  distanceSimulation: true,
  rolloffMode: 'logarithmic',
  matrixUpmix: true,
  cavernize3D: true,
  upmixEffectPct: 75,
  upmixSmoothPct: 80,
  speakerHeightVirt: false,
  dynamicNormalizer: true,
  limiterOnly: true,
  renderGainDb: 0,
  heightSqueezePct: 0,
  roomSize: { x: 10, y: 7, z: 10 },
  quickEqFilters: [],
  quickEqFileName: null,
};

export function App() {
  const engineRef = useRef<CavernSpatialAudioEngine | null>(null);
  if (!engineRef.current) {
    engineRef.current = new CavernSpatialAudioEngine();
  }

  const [activeTab, setActiveTab] = useState<TabId>('play');
  const [tracks, setTracks] = useState<SpatialTrack[]>(DEMO_TRACKS);
  const [activeTrackId, setActiveTrackId] = useState<string>(DEMO_TRACKS[0].id);
  const [activeVariantId, setActiveVariantId] = useState<string>(DEMO_TRACKS[0].variants[0].id);
  const [isPlaying, setIsPlaying] = useState<boolean>(false);
  const [isLooping, setIsLooping] = useState<boolean>(true);
  const [currentTimeSec, setCurrentTimeSec] = useState<number>(18);
  const [masterVolume, setMasterVolume] = useState<number>(0.78);
  const [lfeBoostDb, setLfeBoostDb] = useState<number>(2);
  const [listenerPosition, setListenerPosition] = useState<Vec3>({ x: 0, y: 0, z: 0 });
  const [listenerYawDeg, setListenerYawDeg] = useState<number>(0);
  const [listenerPitchDeg, setListenerPitchDeg] = useState<number>(0);
  const [sources, setSources] = useState<SpatialSourceNode[]>(() => createDefaultSpatialSources());
  const [settings, setSettings] = useState<CavernSettings>(DEFAULT_SETTINGS);
  const [toastMessage, setToastMessage] = useState<string | null>(null);

  const t = useMemo(() => getStrings(settings.locale), [settings.locale]);

  const activeTrack = useMemo(
    () => tracks.find((tr) => tr.id === activeTrackId) ?? tracks[0],
    [tracks, activeTrackId],
  );

  const activeVariant = useMemo(
    () => activeTrack.variants.find((v) => v.id === activeVariantId) ?? activeTrack.variants[0],
    [activeTrack, activeVariantId],
  );

  // Refs for smooth animation loop without re-subscribing
  const stateRef = useRef({
    sources,
    listenerPosition,
    listenerYawDeg,
    listenerPitchDeg,
    masterVolume,
    lfeBoostDb,
    settings,
    activeTrack,
    isLooping,
    currentTimeSec,
  });
  stateRef.current = {
    sources,
    listenerPosition,
    listenerYawDeg,
    listenerPitchDeg,
    masterVolume,
    lfeBoostDb,
    settings,
    activeTrack,
    isLooping,
    currentTimeSec,
  };

  const showToast = (msg: string) => {
    setToastMessage(msg);
  };

  useEffect(() => {
    if (!toastMessage) return;
    const timer = window.setTimeout(() => setToastMessage(null), 2600);
    return () => window.clearTimeout(timer);
  }, [toastMessage]);

  // Cleanup audio engine on unmount
  useEffect(() => {
    const engine = engineRef.current;
    return () => {
      engine?.stop();
    };
  }, []);

  // Playback clock + live 3D object trajectory animation + Web Audio update
  useEffect(() => {
    if (!isPlaying) return;

    let rafId = 0;
    let lastTs = performance.now();

    const tick = (now: number) => {
      const dt = Math.min(0.1, (now - lastTs) / 1000);
      lastTs = now;

      const s = stateRef.current;
      let nextTime = s.currentTimeSec + dt;
      if (nextTime >= s.activeTrack.durationSec) {
        if (s.isLooping) {
          nextTime = 0;
        } else {
          setIsPlaying(false);
          engineRef.current?.stop();
          return;
        }
      }

      setCurrentTimeSec(nextTime);

      // Update animated 3D object positions & RMS meters
      const updatedSources = s.sources.map((src) => {
        const pos = evaluateSourcePosition(src, nextTime);
        const rmsBase = src.kind === 'object' ? 0.48 : src.kind === 'lfe' ? 0.52 : 0.3;
        const rms = clamp(
          rmsBase + 0.28 * Math.sin(nextTime * (src.kind === 'object' ? 2.4 : 1.1) + src.synthFreqHz * 0.02),
          0.08,
          0.95,
        );
        return { ...src, position: pos, rms };
      });
      setSources(updatedSources);

      engineRef.current?.updateFrame(
        updatedSources,
        s.listenerPosition,
        s.listenerYawDeg,
        s.listenerPitchDeg,
        s.masterVolume,
        s.lfeBoostDb,
        s.settings,
        nextTime,
      );

      rafId = window.requestAnimationFrame(tick);
    };

    rafId = window.requestAnimationFrame(tick);
    return () => window.cancelAnimationFrame(rafId);
  }, [isPlaying]);

  const handleTogglePlay = () => {
    if (isPlaying) {
      engineRef.current?.stop();
      setIsPlaying(false);
    } else {
      engineRef.current?.start(
        sources,
        listenerPosition,
        listenerYawDeg,
        listenerPitchDeg,
        masterVolume,
        lfeBoostDb,
        settings,
        activeTrack.localBuffer,
        currentTimeSec,
      );
      setIsPlaying(true);
    }
  };

  const handleSelectTrack = (trackId: string) => {
    const next = tracks.find((tr) => tr.id === trackId);
    if (!next) return;
    setActiveTrackId(next.id);
    setActiveVariantId(next.variants[0].id);
    setCurrentTimeSec(0);
    if (isPlaying) {
      engineRef.current?.start(
        sources,
        listenerPosition,
        listenerYawDeg,
        listenerPitchDeg,
        masterVolume,
        lfeBoostDb,
        settings,
        next.localBuffer,
        0,
      );
    }
  };

  const handleSelectVariant = (variantId: string) => {
    const variant = activeTrack.variants.find((v) => v.id === variantId);
    if (!variant) return;
    setActiveVariantId(variant.id);
    showToast(`Switched to ${variant.label} (position preserved)`);
  };

  const handleSeek = (timeSec: number) => {
    const clamped = clamp(timeSec, 0, activeTrack.durationSec);
    setCurrentTimeSec(clamped);
    setSources((prev) =>
      prev.map((src) => ({ ...src, position: evaluateSourcePosition(src, clamped) })),
    );
    if (isPlaying && activeTrack.localBuffer) {
      engineRef.current?.start(
        sources,
        listenerPosition,
        listenerYawDeg,
        listenerPitchDeg,
        masterVolume,
        lfeBoostDb,
        settings,
        activeTrack.localBuffer,
        clamped,
      );
    }
  };

  const handleSkipRelative = (deltaSec: number) => {
    handleSeek(currentTimeSec + deltaSec);
  };

  const handleStepTrack = (dir: -1 | 1) => {
    const idx = tracks.findIndex((tr) => tr.id === activeTrack.id);
    const nextIdx = (idx + dir + tracks.length) % tracks.length;
    handleSelectTrack(tracks[nextIdx].id);
  };

  const handleUpdateSettings = (patch: Partial<CavernSettings>) => {
    setSettings((prev) => ({ ...prev, ...patch }));
  };

  const handleUploadLocalFile = async (file: File) => {
    try {
      const decoded = await engineRef.current?.decodeAudioFile(file);
      const ext = file.name.split('.').pop()?.toUpperCase() ?? 'AUDIO';
      const newTrack: SpatialTrack = {
        id: `local-${Date.now()}`,
        title: file.name.replace(/\.[^.]+$/, ''),
        artist: 'Local Audio File',
        album: 'Imported Media',
        discNumber: 1,
        trackNumber: tracks.length + 1,
        durationSec: decoded ? Math.max(1, Math.round(decoded.duration)) : 120,
        folder: '/Local Upload',
        isSpatial: (decoded?.numberOfChannels ?? 2) > 2,
        accentHue: 205,
        secondaryHue: 235,
        waveformSeed: file.name.length * 7,
        localBuffer: decoded ?? undefined,
        localFileName: file.name,
        variants: [
          {
            id: `local-v-${Date.now()}`,
            label: `${ext} ${(decoded?.numberOfChannels ?? 2)}ch`,
            codec: `Decoded Web Audio (${ext})`,
            container: ext,
            channels: `${decoded?.numberOfChannels ?? 2} Channels`,
            sampleRateHz: decoded?.sampleRate ?? 48000,
            bitDepth: 24,
            bitrateKbps: 1411,
            isSpatial: (decoded?.numberOfChannels ?? 2) > 2,
            objectCount: 0,
          },
        ],
        lyrics: [],
      };
      setTracks((prev) => [newTrack, ...prev]);
      setActiveTrackId(newTrack.id);
      setActiveVariantId(newTrack.variants[0].id);
      setCurrentTimeSec(0);
      showToast(`Loaded ${file.name}`);
    } catch {
      showToast(`Could not decode ${file.name} in browser audio decoder`);
    }
  };

  const handleToggleSolo = (sourceId: string) => {
    setSources((prev) =>
      prev.map((src) => (src.id === sourceId ? { ...src, solo: !src.solo } : src)),
    );
  };

  const handleToggleMute = (sourceId: string) => {
    setSources((prev) =>
      prev.map((src) => (src.id === sourceId ? { ...src, mute: !src.mute } : src)),
    );
  };

  const handleResetSoloMute = () => {
    setSources((prev) => prev.map((src) => ({ ...src, solo: false, mute: false })));
  };

  const handleResetRoom = () => {
    setListenerPosition({ x: 0, y: 0, z: 0 });
    setListenerYawDeg(0);
    setListenerPitchDeg(0);
  };

  const navTabs: Array<{ id: TabId; label: string }> = [
    { id: 'play', label: t.tabPlay },
    { id: 'room', label: t.tabRoom },
    { id: 'studio', label: t.tabStudio },
    { id: 'stems', label: t.tabStems },
    { id: 'settings', label: t.tabSettings },
  ];

  return (
    <div className="min-h-screen flex flex-col bg-[#080A10] text-[#FCFCFF]">
      {/* Top Navigation Bar — clean 3-zone contract on desktop, clean 2-row on narrow mobile */}
      <header className="sticky top-0 z-30 bg-[#080A10]/95 backdrop-blur-md border-b border-white/10">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 h-14 md:h-16 flex items-center justify-between gap-6">
          {/* Zone 1: Single-element brand wordmark */}
          <a
            href="#top"
            onClick={(e) => {
              e.preventDefault();
              setActiveTab('play');
            }}
            className="text-lg font-bold tracking-tight text-white whitespace-nowrap shrink-0"
          >
            {t.appTitle}
          </a>

          {/* Zone 2 (Desktop): 5 concise single-line navigation tabs */}
          <nav
            aria-label="Primary workspaces"
            className="hidden md:flex items-center gap-6"
          >
            {navTabs.map((tab) => {
              const active = activeTab === tab.id;
              return (
                <button
                  key={tab.id}
                  type="button"
                  aria-current={active ? 'page' : undefined}
                  onClick={() => setActiveTab(tab.id)}
                  className={`px-2 py-2 text-sm font-medium transition-colors whitespace-nowrap shrink-0 border-b-2 ${
                    active
                      ? 'text-white border-[#387AFF]'
                      : 'text-[#99999E] hover:text-white border-transparent'
                  }`}
                >
                  {tab.label}
                </button>
              );
            })}
          </nav>

          {/* Zone 3: 1 primary action */}
          <div className="flex items-center shrink-0">
            <button
              type="button"
              onClick={handleTogglePlay}
              className="inline-flex items-center gap-2 px-4 py-2 text-xs font-semibold rounded-lg bg-[#387AFF] hover:bg-[#598FFF] text-white transition-colors whitespace-nowrap shrink-0"
            >
              {isPlaying ? <Pause className="w-3.5 h-3.5" /> : <Play className="w-3.5 h-3.5" />}
              <span>{isPlaying ? 'Pause' : 'Play'}</span>
            </button>
          </div>
        </div>

        {/* Mobile compact 5-tab strip (zero clipping, total header <= 100px / 11.8% of 844px) */}
        <nav
          aria-label="Mobile workspaces"
          className="md:hidden grid grid-cols-5 border-t border-white/8 px-2"
        >
          {navTabs.map((tab) => {
            const active = activeTab === tab.id;
            return (
              <button
                key={tab.id}
                type="button"
                aria-current={active ? 'page' : undefined}
                onClick={() => setActiveTab(tab.id)}
                className={`py-2.5 text-xs font-medium text-center transition-colors whitespace-nowrap border-b-2 ${
                  active
                    ? 'text-white border-[#387AFF]'
                    : 'text-[#99999E] hover:text-white border-transparent'
                }`}
              >
                {tab.label}
              </button>
            );
          })}
        </nav>
      </header>

      {/* Main Workspace */}
      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 py-6 sm:py-8">
        {activeTab === 'play' && (
          <PlayTab
            t={t}
            tracks={tracks}
            activeTrack={activeTrack}
            activeVariant={activeVariant}
            isPlaying={isPlaying}
            isLooping={isLooping}
            currentTimeSec={currentTimeSec}
            masterVolume={masterVolume}
            settings={settings}
            onSelectTrack={handleSelectTrack}
            onSelectVariant={handleSelectVariant}
            onTogglePlay={handleTogglePlay}
            onSeek={handleSeek}
            onSkipRelative={handleSkipRelative}
            onPrevTrack={() => handleStepTrack(-1)}
            onNextTrack={() => handleStepTrack(1)}
            onToggleLoop={() => setIsLooping((v) => !v)}
            onVolumeChange={setMasterVolume}
            onUpdateSettings={handleUpdateSettings}
            onOpenRoomTab={() => setActiveTab('room')}
            onUploadLocalFile={handleUploadLocalFile}
          />
        )}

        {activeTab === 'room' && (
          <RoomTab
            t={t}
            sources={sources}
            listenerPosition={listenerPosition}
            listenerYawDeg={listenerYawDeg}
            listenerPitchDeg={listenerPitchDeg}
            lfeBoostDb={lfeBoostDb}
            isPlaying={isPlaying}
            settings={settings}
            onListenerPositionChange={setListenerPosition}
            onListenerOrientationChange={(yaw, pitch) => {
              setListenerYawDeg(yaw);
              setListenerPitchDeg(pitch);
            }}
            onResetRoom={handleResetRoom}
            onLfeBoostChange={setLfeBoostDb}
            onUpdateSettings={handleUpdateSettings}
            onToggleSolo={handleToggleSolo}
            onToggleMute={handleToggleMute}
            onResetSoloMute={handleResetSoloMute}
          />
        )}

        {activeTab === 'studio' && (
          <StudioTab
            t={t}
            activeTrack={activeTrack}
            activeVariant={activeVariant}
            sources={sources}
            listenerPosition={listenerPosition}
            settings={settings}
          />
        )}

        {activeTab === 'stems' && (
          <StemsTab t={t} activeTrack={activeTrack} sources={sources} />
        )}

        {activeTab === 'settings' && (
          <SettingsTab
            t={t}
            settings={settings}
            onUpdateSettings={handleUpdateSettings}
          />
        )}
      </main>

      {/* Quiet footer with active track readout */}
      <footer className="border-t border-white/8 bg-[#0A0C12]">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 py-3 flex flex-wrap items-center justify-between gap-2 text-xs text-[#99999E]">
          <span>Cavernize Spatial Audio Studio · Built on the Cavern Acoustic Architecture</span>
          <span className="font-mono tabular-nums text-[#B7B7BB]">
            {activeTrack.title} · {formatTime(currentTimeSec)} / {formatTime(activeTrack.durationSec)}
          </span>
        </div>
      </footer>

      {/* Transient feedback toast */}
      {toastMessage && (
        <div
          role="status"
          aria-live="polite"
          className="fixed bottom-5 right-5 z-50 px-4 py-2.5 rounded-xl bg-[#1E1F27] border border-[#598FFF]/40 text-xs font-medium text-white shadow-lg"
        >
          {toastMessage}
        </div>
      )}
    </div>
  );
}

export default App;
