import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  CheckCircle2,
  Download,
  FileAudio,
  ListPlus,
  Play,
  Trash2,
} from 'lucide-react';
import { renderSpatialMixWav } from '../audio/spatialEngine';
import { SPEAKER_LAYOUTS } from '../data/catalog';
import type { StringKey } from '../i18n/strings';
import type {
  CavernSettings,
  PostRenderReport,
  RenderQueueItem,
  SpatialSourceNode,
  SpatialTrack,
  TrackFormatVariant,
  Vec3,
} from '../types/cavern';
import { LayoutDiagram } from './LayoutDiagram';
import { ScarlettVocalConsole } from './ScarlettVocalConsole';
import type {
  ScarlettHardwareControlState,
  VocalRackState,
} from '../utils/scarlettVocalDsp';

interface StudioTabProps {
  t: Record<StringKey, string>;
  activeTrack: SpatialTrack;
  activeVariant: TrackFormatVariant;
  sources: SpatialSourceNode[];
  listenerPosition: Vec3;
  listenerYawDeg: number;
  lfeBoostDb: number;
  settings: CavernSettings;
  studioSubMode: 'scarlett-vocal' | 'converter';
  onStudioSubModeChange: (mode: 'scarlett-vocal' | 'converter') => void;
  scarlett: ScarlettHardwareControlState;
  vocalRack: VocalRackState;
  onUpdateScarlett: (next: ScarlettHardwareControlState) => void;
  onUpdateVocalRack: (patch: Partial<VocalRackState>) => void;
  onNotify: (msg: string) => void;
}

const OUTPUT_CODECS = [
  'PCM 16-bit (WAV)',
  'PCM 24-bit (WAV)',
  'PCM 32-bit Float',
  'FLAC Lossless',
  'E-AC-3 Surround (768 kbps)',
];

const CONTAINERS = ['RIFF WAVE (.wav)', 'ADM BWF (.wav)', 'DAMF (.atmos)', 'Core Audio (.caf)'];

export const StudioTab: React.FC<StudioTabProps> = ({
  t,
  activeTrack,
  activeVariant,
  sources,
  listenerPosition,
  listenerYawDeg,
  lfeBoostDb,
  settings,
  studioSubMode,
  onStudioSubModeChange,
  scarlett,
  vocalRack,
  onUpdateScarlett,
  onUpdateVocalRack,
  onNotify,
}) => {
  const [selectedLayoutId, setSelectedLayoutId] = useState<string>('7.1.4');
  const [outputCodec, setOutputCodec] = useState<string>(OUTPUT_CODECS[1]);
  const [container, setContainer] = useState<string>(CONTAINERS[0]);
  const [bakeRoomState, setBakeRoomState] = useState<boolean>(true);
  const [showMetadata, setShowMetadata] = useState<boolean>(false);
  const [report, setReport] = useState<PostRenderReport | null>(null);
  const [queue, setQueue] = useState<RenderQueueItem[]>([]);
  const blobUrlsRef = useRef<Set<string>>(new Set());

  useEffect(() => {
    const urls = blobUrlsRef.current;
    return () => {
      for (const url of urls) {
        URL.revokeObjectURL(url);
      }
      urls.clear();
    };
  }, []);

  const selectedLayout = useMemo(
    () => SPEAKER_LAYOUTS.find((l) => l.id === selectedLayoutId) ?? SPEAKER_LAYOUTS[0],
    [selectedLayoutId],
  );

  const handleRenderNow = () => {
    const result = renderSpatialMixWav(
      activeTrack.title,
      selectedLayout,
      outputCodec,
      container,
      sources,
      listenerPosition,
      listenerYawDeg,
      lfeBoostDb,
      bakeRoomState,
      settings.rolloffMode,
    );
    blobUrlsRef.current.add(result.downloadUrl);
    setReport(result);
  };

  const handleAddToQueue = () => {
    const item: RenderQueueItem = {
      id: `${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
      trackTitle: activeTrack.title,
      sourceFormat: activeVariant.label,
      targetLayoutId: selectedLayout.id,
      targetLayoutName: selectedLayout.name,
      outputCodec,
      container,
      bakeRoomState,
      status: 'queued',
      progress: 0,
    };
    setQueue((prev) => [...prev, item]);
  };

  const handleProcessQueue = () => {
    if (queue.length === 0) return;
    let latestReport: PostRenderReport | null = null;
    const processed = queue.map((item) => {
      if (item.status === 'done' && item.report) {
        latestReport = item.report;
        return item;
      }
      const layout =
        SPEAKER_LAYOUTS.find((l) => l.id === item.targetLayoutId) ?? selectedLayout;
      const rendered = renderSpatialMixWav(
        item.trackTitle,
        layout,
        item.outputCodec,
        item.container,
        sources,
        listenerPosition,
        listenerYawDeg,
        lfeBoostDb,
        item.bakeRoomState,
        settings.rolloffMode,
      );
      blobUrlsRef.current.add(rendered.downloadUrl);
      latestReport = rendered;
      return { ...item, status: 'done' as const, progress: 100, report: rendered };
    });
    setQueue(processed);
    if (latestReport) {
      setReport(latestReport);
    }
  };

  return (
    <section aria-label={t.tabStudio} className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div
          role="tablist"
          aria-label="Studio mode"
          className="inline-flex items-center gap-1 p-1 rounded-xl bg-[#15161C] border border-white/10"
        >
          <button
            type="button"
            role="tab"
            aria-selected={studioSubMode === 'scarlett-vocal'}
            onClick={() => onStudioSubModeChange('scarlett-vocal')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-colors whitespace-nowrap shrink-0 ${
              studioSubMode === 'scarlett-vocal'
                ? 'bg-[#387AFF] text-white'
                : 'text-[#B7B7BB] hover:text-white'
            }`}
          >
            {t.studioModeScarlettVocal}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={studioSubMode === 'converter'}
            onClick={() => onStudioSubModeChange('converter')}
            className={`px-4 py-2 text-sm font-medium rounded-lg transition-colors whitespace-nowrap shrink-0 ${
              studioSubMode === 'converter'
                ? 'bg-[#387AFF] text-white'
                : 'text-[#B7B7BB] hover:text-white'
            }`}
          >
            {t.studioModeConverter}
          </button>
        </div>

        {studioSubMode === 'converter' && (
          <button
            type="button"
            onClick={() => setShowMetadata((v) => !v)}
            className="px-3.5 py-2 text-xs font-medium rounded-lg bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 transition-colors whitespace-nowrap shrink-0"
          >
            {t.codecMetadata}
          </button>
        )}
      </div>

      {studioSubMode === 'scarlett-vocal' ? (
        <ScarlettVocalConsole
          t={t}
          activeTrack={activeTrack}
          scarlett={scarlett}
          vocalRack={vocalRack}
          onUpdateScarlett={onUpdateScarlett}
          onUpdateVocalRack={onUpdateVocalRack}
          onNotify={onNotify}
        />
      ) : (
        <>
          <div>
            <h2 className="text-xl font-bold text-white">{t.studioTitle}</h2>
            <p className="text-sm text-[#99999E] mt-0.5">
              {t.activeSourceLabel}: <span className="text-[#E9E9EC] font-medium">{activeTrack.title}</span> ·{' '}
              <span className="font-mono text-xs">{activeVariant.codec}</span>
            </p>
          </div>

          {showMetadata && (
        <div className="bg-[#15161C] border border-white/10 rounded-2xl p-4 grid grid-cols-2 sm:grid-cols-4 gap-4 text-xs">
          <div>
            <p className="text-[#99999E]">{t.metaCodec}</p>
            <p className="font-mono text-white mt-1">{activeVariant.codec}</p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.metaContainer}</p>
            <p className="font-mono text-white mt-1">{activeVariant.container}</p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.metaRateDepth}</p>
            <p className="font-mono tabular-nums text-white mt-1">
              {activeVariant.sampleRateHz} Hz · {activeVariant.bitDepth}-bit
            </p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.metaBedObjects}</p>
            <p className="font-mono text-white mt-1">{activeVariant.channels}</p>
          </div>
        </div>
      )}

      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
        {/* Left 7 cols: Speaker Target Picker + Export Configuration */}
        <div className="xl:col-span-7 space-y-5">
          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
            <h3 className="text-sm font-semibold text-white">{t.targetLayout}</h3>

            {(
              [
                ['pc', t.catPc],
                ['matrix', t.catMatrix],
                ['multichannel', t.catMultichannel],
              ] as const
            ).map(([catKey, catTitle]) => {
              const items = SPEAKER_LAYOUTS.filter((l) => l.category === catKey);
              return (
                <div key={catKey} className="space-y-2">
                  <p className="text-xs font-medium text-[#99999E]">{catTitle}</p>
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
                    {items.map((layout) => {
                      const active = layout.id === selectedLayoutId;
                      return (
                        <button
                          key={layout.id}
                          type="button"
                          onClick={() => setSelectedLayoutId(layout.id)}
                          className={`p-3 rounded-xl border text-left transition-colors ${
                            active
                              ? 'bg-[#387AFF]/18 border-[#598FFF] text-white'
                              : 'bg-[#1C1E27] border-white/8 text-[#B7B7BB] hover:text-white'
                          }`}
                        >
                          <div className="flex items-center justify-between gap-2">
                            <span className="text-sm font-semibold truncate">{layout.name}</span>
                            <span className="text-xs font-mono tabular-nums text-[#89B0FF] shrink-0">
                              {layout.channelCount}ch
                            </span>
                          </div>
                          <p className="text-xs text-[#99999E] mt-1 line-clamp-2">
                            {layout.description}
                          </p>
                        </button>
                      );
                    })}
                  </div>
                </div>
              );
            })}
          </div>

          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <label htmlFor="studio-codec" className="text-xs font-medium text-[#99999E] block">
                  {t.outputCodec}
                </label>
                <select
                  id="studio-codec"
                  value={outputCodec}
                  onChange={(e) => setOutputCodec(e.target.value)}
                  className="w-full px-3 py-2 text-sm rounded-xl bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
                >
                  {OUTPUT_CODECS.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </select>
              </div>

              <div className="space-y-1.5">
                <label htmlFor="studio-container" className="text-xs font-medium text-[#99999E] block">
                  {t.containerFormat}
                </label>
                <select
                  id="studio-container"
                  value={container}
                  onChange={(e) => setContainer(e.target.value)}
                  className="w-full px-3 py-2 text-sm rounded-xl bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
                >
                  {CONTAINERS.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <label className="flex items-start gap-2.5 text-sm text-[#E9E9EC] cursor-pointer pt-1">
              <input
                type="checkbox"
                checked={bakeRoomState}
                onChange={(e) => setBakeRoomState(e.target.checked)}
                className="mt-1 rounded accent-[#387AFF]"
              />
              <span>{t.bakeRoomToggle}</span>
            </label>

            <div className="flex flex-wrap items-center gap-3 pt-2">
              <button
                type="button"
                onClick={handleRenderNow}
                className="inline-flex items-center gap-2 px-5 py-2.5 text-sm font-semibold rounded-xl bg-[#387AFF] hover:bg-[#598FFF] text-white transition-colors whitespace-nowrap shrink-0"
              >
                <FileAudio className="w-4 h-4" />
                <span>{t.renderExport}</span>
              </button>

              <button
                type="button"
                onClick={handleAddToQueue}
                className="inline-flex items-center gap-2 px-4 py-2.5 text-sm font-medium rounded-xl bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 transition-colors whitespace-nowrap shrink-0"
              >
                <ListPlus className="w-4 h-4 text-[#6E9CFF]" />
                <span>{t.addToQueue}</span>
              </button>
            </div>
          </div>
        </div>

        {/* Right 5 cols: Wiring Diagram + Post-Render Report + Batch Queue */}
        <div className="xl:col-span-5 space-y-5">
          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-3">
            <h3 className="text-sm font-semibold text-white">{t.wiringGuide}</h3>
            <LayoutDiagram layout={selectedLayout} channelsLabel={t.channelsWord} />
          </div>

          {report && (
            <div className="bg-[#15161C] border border-[#34D399]/35 rounded-2xl p-5 space-y-4">
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <CheckCircle2 className="w-4 h-4 text-[#34D399]" />
                  <h3 className="text-sm font-semibold text-white">{t.postRenderReport}</h3>
                </div>
                <span className="text-xs font-mono tabular-nums text-[#34D399]">
                  {report.fileSizeKb} KB WAV
                </span>
              </div>

              <div className="grid grid-cols-3 gap-3 text-xs">
                <div className="p-2.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  <p className="text-[#99999E]">{t.metricTruePeak}</p>
                  <p className="font-mono tabular-nums text-white font-semibold mt-1">
                    {report.peakDbfs} dBFS
                  </p>
                </div>
                <div className="p-2.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  <p className="text-[#99999E]">{t.metricIntegrated}</p>
                  <p className="font-mono tabular-nums text-white font-semibold mt-1">
                    {report.rmsLufs} LUFS
                  </p>
                </div>
                <div className="p-2.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  <p className="text-[#99999E]">{t.metricHeadroom}</p>
                  <p className="font-mono tabular-nums text-[#34D399] font-semibold mt-1">
                    +{report.headroomDb} dB
                  </p>
                </div>
              </div>

              <div className="text-xs text-[#B7B7BB] flex flex-wrap gap-x-3 gap-y-1 font-mono tabular-nums">
                <span>{report.targetLayout}</span>
                <span>·</span>
                <span>{report.sampleRateHz} Hz</span>
                <span>·</span>
                <span>{report.bakedRoomPosition ? t.bakedRoomBadge : t.neutralOriginBadge}</span>
              </div>

              <a
                href={report.downloadUrl}
                download={report.downloadFileName}
                className="w-full inline-flex items-center justify-center gap-2 px-4 py-2.5 text-sm font-semibold rounded-xl bg-[#34D399] hover:bg-[#22C58B] text-[#080A10] transition-colors"
              >
                <Download className="w-4 h-4" />
                <span>{t.downloadFile.replace('{file}', report.downloadFileName)}</span>
              </a>
            </div>
          )}

          {/* Batch Render Queue */}
          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-3">
            <div className="flex items-center justify-between gap-2">
              <h3 className="text-sm font-semibold text-white">
                {t.renderQueue} ({queue.length})
              </h3>
              {queue.length > 0 && (
                <button
                  type="button"
                  onClick={handleProcessQueue}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg bg-[#387AFF] hover:bg-[#598FFF] text-white whitespace-nowrap shrink-0"
                >
                  <Play className="w-3 h-3" />
                  <span>{t.processQueue}</span>
                </button>
              )}
            </div>

            {queue.length === 0 ? (
              <p className="text-xs text-[#99999E] py-3">
                {t.queueEmpty}
              </p>
            ) : (
              <div className="space-y-2">
                {queue.map((item) => (
                  <div
                    key={item.id}
                    className="flex items-center justify-between gap-3 p-3 rounded-xl bg-[#1C1E27] border border-white/8 text-xs"
                  >
                    <div className="min-w-0">
                      <p className="font-semibold text-white truncate">{item.trackTitle}</p>
                      <p className="text-[#99999E] mt-0.5 font-mono truncate">
                        {item.targetLayoutName} · {item.outputCodec}
                      </p>
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      {item.status === 'done' && item.report ? (
                        <a
                          href={item.report.downloadUrl}
                          download={item.report.downloadFileName}
                          aria-label={`Download ${item.report.downloadFileName}`}
                          className="inline-flex items-center gap-1 px-2 py-1 rounded-md bg-[#34D399]/18 hover:bg-[#34D399]/30 text-[#34D399] font-mono font-semibold"
                        >
                          <Download className="w-3 h-3" />
                          <span>WAV</span>
                        </a>
                      ) : (
                        <span className="font-mono text-[#89B0FF]">{t.statusQueued}</span>
                      )}
                      <button
                        type="button"
                        aria-label={`Remove ${item.trackTitle} from queue`}
                        onClick={() => setQueue((prev) => prev.filter((q) => q.id !== item.id))}
                        className="p-1.5 rounded-md text-[#99999E] hover:text-[#F56962] hover:bg-white/5"
                      >
                        <Trash2 className="w-3.5 h-3.5" />
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>
        </>
      )}
    </section>
  );
};
