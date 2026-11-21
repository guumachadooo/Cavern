import React, { useEffect, useRef, useState } from 'react';
import {
  CheckCircle2,
  Download,
  FileAudio,
  FileSearch,
  Layers,
  ListPlus,
  Play,
  Trash2,
} from 'lucide-react';
import type { StringKey } from '../i18n/strings';
import type { SpatialSourceNode, SpatialTrack, TrackFormatVariant, Vec3 } from '../types/cavern';
import {
  inspectIamfObuBytes,
  inspectSpatialWaveBytes,
  renderW12MasterArtifact,
  W12_STUDIO_PRESETS,
  type W12MasterArtifact,
  type W12StudioPresetId,
} from '../utils/w12Formats';

interface W12BatchJob {
  id: string;
  presetId: W12StudioPresetId;
  presetLabel: string;
  extension: string;
  hoaOrder: number;
  karaokeVocalPct: number;
  ceilingDbtp: number;
  status: 'queued' | 'done';
  artifact?: W12MasterArtifact;
  downloadUrl?: string;
}

interface W12StudioMasterPanelProps {
  t: Record<StringKey, string>;
  activeTrack: SpatialTrack;
  activeVariant: TrackFormatVariant;
  sources: SpatialSourceNode[];
  listenerPosition: Vec3;
  onNotify: (msg: string) => void;
}

export const W12StudioMasterPanel: React.FC<W12StudioMasterPanelProps> = ({
  t,
  activeTrack,
  activeVariant,
  sources,
  listenerPosition,
  onNotify,
}) => {
  const [selectedPresetId, setSelectedPresetId] = useState<W12StudioPresetId>('eclipsa-iamf');
  const [hoaOrder, setHoaOrder] = useState<number>(3);
  const [karaokeVocalPct, setKaraokeVocalPct] = useState<number>(5);
  const [ceilingDbtp, setCeilingDbtp] = useState<number>(-1.0);
  const [latestArtifact, setLatestArtifact] = useState<{
    artifact: W12MasterArtifact;
    downloadUrl: string;
  } | null>(null);
  const [externalInspection, setExternalInspection] = useState<{
    fileName: string;
    badge: string;
    detail: string;
  } | null>(null);
  const [batchQueue, setBatchQueue] = useState<W12BatchJob[]>([]);

  const blobUrlsRef = useRef<Set<string>>(new Set());
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    const urls = blobUrlsRef.current;
    return () => {
      for (const u of urls) URL.revokeObjectURL(u);
      urls.clear();
    };
  }, []);

  const getPresetLabel = (id: W12StudioPresetId): string => {
    switch (id) {
      case 'eclipsa-iamf':
        return t.w12PresetEclipsa;
      case 'ambisonics-hoa3':
        return t.w12PresetHoa3;
      case 'adm-bwf':
        return t.w12PresetAdm;
      case 'personalized-binaural':
        return t.w12PresetBinaural;
      case 'hybrid-neural-714':
        return t.w12PresetHybrid714;
      case 'spatial-karaoke':
        return t.w12PresetKaraoke;
      case 'live-karaoke-mixdown':
        return t.w12PresetLiveMix;
      case 'flac-atmos-hybrid':
        return t.w12PresetFlacAtmos;
    }
  };

  const makeBlobUrl = (artifact: W12MasterArtifact): string => {
    const copy = new Uint8Array(artifact.bytes.byteLength);
    copy.set(artifact.bytes);
    const blob = new Blob([copy.buffer], { type: artifact.mimeType });
    const url = URL.createObjectURL(blob);
    blobUrlsRef.current.add(url);
    return url;
  };

  const handleExportNow = () => {
    const artifact = renderW12MasterArtifact({
      presetId: selectedPresetId,
      trackTitle: activeTrack.title,
      sources,
      listenerPosition,
      hoaOrder,
      karaokeVocalPct,
      truePeakCeilingDbtp: ceilingDbtp,
      durationSec: 1.5,
    });
    const downloadUrl = makeBlobUrl(artifact);
    setLatestArtifact({ artifact, downloadUrl });
    setExternalInspection(null);
    onNotify(
      t.w12ExportedToast
        .replace('{file}', artifact.fileName)
        .replace('{badge}', artifact.verificationBadge),
    );
  };

  const handleQueuePreset = () => {
    const spec = W12_STUDIO_PRESETS.find((p) => p.id === selectedPresetId) ?? W12_STUDIO_PRESETS[0];
    const job: W12BatchJob = {
      id: `${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
      presetId: selectedPresetId,
      presetLabel: getPresetLabel(selectedPresetId),
      extension: spec.extension,
      hoaOrder,
      karaokeVocalPct,
      ceilingDbtp,
      status: 'queued',
    };
    setBatchQueue((prev) => [...prev, job]);
  };

  const handleProcessBatch = () => {
    if (batchQueue.length === 0) return;
    let lastRendered: { artifact: W12MasterArtifact; downloadUrl: string } | null = null;
    const updated: W12BatchJob[] = [];
    for (const job of batchQueue) {
      if (job.status === 'done' && job.artifact && job.downloadUrl) {
        lastRendered = { artifact: job.artifact, downloadUrl: job.downloadUrl };
        updated.push(job);
        continue;
      }
      const artifact = renderW12MasterArtifact({
        presetId: job.presetId,
        trackTitle: activeTrack.title,
        sources,
        listenerPosition,
        hoaOrder: job.hoaOrder,
        karaokeVocalPct: job.karaokeVocalPct,
        truePeakCeilingDbtp: job.ceilingDbtp,
        durationSec: 1.2,
      });
      const downloadUrl = makeBlobUrl(artifact);
      lastRendered = { artifact, downloadUrl };
      updated.push({ ...job, status: 'done' as const, artifact, downloadUrl });
    }
    setBatchQueue(updated);
    if (lastRendered) {
      setLatestArtifact(lastRendered);
      setExternalInspection(null);
      onNotify(
        t.w12ExportedToast
          .replace('{file}', lastRendered.artifact.fileName)
          .replace('{badge}', lastRendered.artifact.verificationBadge),
      );
    }
  };

  const handleInspectFile = async (file: File) => {
    const buf = new Uint8Array(await file.arrayBuffer());
    const iamf = inspectIamfObuBytes(buf);
    if (iamf.valid) {
      const detail = `magic="${iamf.magic}" · ${iamf.codecFourCc} · ${iamf.elementType} (Order ${iamf.hoaOrder}, ${iamf.channelCount}ch) · ${iamf.obuCount} OBUs · ${iamf.integratedLufs} LUFS`;
      setExternalInspection({
        fileName: file.name,
        badge: 'IAMF v1.0 OBU Verified',
        detail,
      });
      onNotify(
        t.w12InspectedExternalToast
          .replace('{file}', file.name)
          .replace('{summary}', 'IAMF v1.0 OBU'),
      );
      return;
    }

    const wave = inspectSpatialWaveBytes(buf);
    if (wave.valid) {
      const detail = wave.hasAxml
        ? `${wave.formatKind} · axml + chna · ${wave.admObjectCount} objects · ${wave.channelCount}ch @ ${wave.sampleRateHz} Hz`
        : wave.isAmbisonicGuid
          ? `${wave.formatKind} · Order ${wave.hoaOrder} ACN/SN3D · ${wave.channelCount}ch @ ${wave.sampleRateHz} Hz`
          : `RIFF WAVE PCM · ${wave.channelCount}ch @ ${wave.sampleRateHz} Hz (${wave.bitDepth}-bit)`;
      setExternalInspection({
        fileName: file.name,
        badge: wave.formatKind,
        detail,
      });
      onNotify(
        t.w12InspectedExternalToast
          .replace('{file}', file.name)
          .replace('{summary}', wave.formatKind),
      );
      return;
    }

    setExternalInspection({
      fileName: file.name,
      badge: 'Unrecognized Container',
      detail: `${Math.max(1, Math.round(buf.length / 1024))} KB · Not a valid IAMF OBU or RIFF WAVE stream`,
    });
  };

  const selectedSpec =
    W12_STUDIO_PRESETS.find((p) => p.id === selectedPresetId) ?? W12_STUDIO_PRESETS[0];

  return (
    <div className="space-y-6">
      {/* Header Row */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-xl font-bold text-white flex items-center gap-2">
            <Layers className="w-5 h-5 text-[#598FFF] shrink-0" />
            <span>{t.w12Title}</span>
          </h2>
          <p className="text-sm text-[#99999E] mt-1">{t.w12Subtitle}</p>
          <p className="text-xs text-[#99999E] mt-1">
            {t.activeSourceLabel}: <span className="text-[#E9E9EC] font-medium">{activeTrack.title}</span> ·{' '}
            <span className="font-mono">{activeVariant.codec}</span>
          </p>
        </div>

        <div className="shrink-0">
          <input
            ref={fileInputRef}
            type="file"
            accept=".iamf,.amb,.wav"
            className="hidden"
            onChange={(e) => {
              const f = e.target.files?.[0];
              if (f) void handleInspectFile(f);
              e.target.value = '';
            }}
          />
          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            className="inline-flex items-center gap-2 px-3.5 py-2 text-xs font-medium rounded-xl bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 transition-colors whitespace-nowrap"
          >
            <FileSearch className="w-4 h-4 text-[#6E9CFF]" />
            <span>{t.w12InspectFileBtn}</span>
          </button>
        </div>
      </div>

      {externalInspection && (
        <div className="bg-[#15161C] border border-[#598FFF]/40 rounded-2xl p-4 flex flex-wrap items-center justify-between gap-3 text-xs">
          <div className="space-y-1 min-w-0">
            <p className="text-white font-semibold truncate">{externalInspection.fileName}</p>
            <p className="font-mono text-[#B7B7BB] break-all">{externalInspection.detail}</p>
          </div>
          <span className="px-2.5 py-1 rounded-lg bg-[#387AFF]/20 text-[#89B0FF] font-mono font-semibold shrink-0">
            {externalInspection.badge}
          </span>
        </div>
      )}

      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
        {/* Left 7 cols: 8 Studio Master Workflows + DSP Mastering Options */}
        <div className="xl:col-span-7 space-y-5">
          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
            <h3 className="text-sm font-semibold text-white">{t.w12PresetLabel}</h3>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
              {W12_STUDIO_PRESETS.map((preset) => {
                const active = preset.id === selectedPresetId;
                return (
                  <button
                    key={preset.id}
                    type="button"
                    aria-pressed={active}
                    onClick={() => setSelectedPresetId(preset.id)}
                    className={`p-3.5 rounded-xl border text-left transition-colors ${
                      active
                        ? 'bg-[#387AFF]/18 border-[#598FFF] text-white'
                        : 'bg-[#1C1E27] border-white/8 text-[#B7B7BB] hover:text-white'
                    }`}
                  >
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-sm font-semibold truncate">
                        {getPresetLabel(preset.id)}
                      </span>
                      <span className="text-xs font-mono tabular-nums text-[#89B0FF] shrink-0">
                        {preset.extension}
                      </span>
                    </div>
                    <p className="text-xs text-[#99999E] mt-1 line-clamp-2">
                      {preset.pipelineSummary}
                    </p>
                  </button>
                );
              })}
            </div>
          </div>

          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
              <div className="space-y-1.5">
                <label htmlFor="w12-hoa-order" className="text-xs font-medium text-[#99999E] block">
                  {t.w12HoaOrderLabel}
                </label>
                <select
                  id="w12-hoa-order"
                  value={hoaOrder}
                  onChange={(e) => setHoaOrder(Number(e.target.value))}
                  className="w-full px-3 py-2 text-sm rounded-xl bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
                >
                  <option value={1}>Order 1 (4ch ACN/SN3D)</option>
                  <option value={2}>Order 2 (9ch ACN/SN3D)</option>
                  <option value={3}>Order 3 (16ch ACN/SN3D)</option>
                </select>
              </div>

              <div className="space-y-1.5">
                <label htmlFor="w12-ceiling" className="text-xs font-medium text-[#99999E] block">
                  {t.w12CeilingLabel}
                </label>
                <select
                  id="w12-ceiling"
                  value={ceilingDbtp}
                  onChange={(e) => setCeilingDbtp(Number(e.target.value))}
                  className="w-full px-3 py-2 text-sm rounded-xl bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
                >
                  <option value={-1.0}>-1.0 dBTP (EBU R128)</option>
                  <option value={-0.5}>-0.5 dBTP (Master)</option>
                  <option value={-0.1}>-0.1 dBTP (Brickwall)</option>
                </select>
              </div>

              <div className="space-y-1.5">
                <div className="flex items-center justify-between text-xs">
                  <label htmlFor="w12-karaoke-slider" className="font-medium text-[#99999E]">
                    {t.w12KaraokeAttenLabel}
                  </label>
                  <span className="font-mono tabular-nums text-white">{karaokeVocalPct}%</span>
                </div>
                <input
                  id="w12-karaoke-slider"
                  type="range"
                  min={0}
                  max={100}
                  step={5}
                  value={karaokeVocalPct}
                  onChange={(e) => setKaraokeVocalPct(Number(e.target.value))}
                  className="w-full accent-[#387AFF] mt-2"
                />
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-3 pt-1">
              <button
                type="button"
                onClick={handleExportNow}
                className="w-full sm:w-auto inline-flex items-center justify-center gap-2 px-5 py-2.5 text-sm font-semibold rounded-xl bg-[#387AFF] hover:bg-[#598FFF] text-white transition-colors whitespace-nowrap shrink-0"
              >
                <FileAudio className="w-4 h-4" />
                <span>{t.w12ExportNowBtn}</span>
              </button>

              <button
                type="button"
                onClick={handleQueuePreset}
                className="w-full sm:w-auto inline-flex items-center justify-center gap-2 px-4 py-2.5 text-sm font-medium rounded-xl bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 transition-colors whitespace-nowrap shrink-0"
              >
                <ListPlus className="w-4 h-4 text-[#6E9CFF]" />
                <span>{t.w12QueuePresetBtn}</span>
              </button>

              <span className="text-xs font-mono text-[#99999E] sm:ml-auto">
                {selectedSpec.formatTag} ·{' '}
                {selectedPresetId === 'eclipsa-iamf' || selectedPresetId === 'ambisonics-hoa3'
                  ? (hoaOrder + 1) * (hoaOrder + 1)
                  : selectedSpec.channels}
                ch
              </span>
            </div>
          </div>
        </div>

        {/* Right 5 cols: Binary Format Verification Report + W12 Batch Queue */}
        <div className="xl:col-span-5 space-y-5">
          {latestArtifact && (
            <div className="bg-[#15161C] border border-[#34D399]/35 rounded-2xl p-5 space-y-4">
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2 min-w-0">
                  <CheckCircle2 className="w-4 h-4 text-[#34D399] shrink-0" />
                  <h3 className="text-sm font-semibold text-white truncate">
                    {t.w12BinaryVerifyTitle}
                  </h3>
                </div>
                <span className="text-xs font-mono tabular-nums text-[#34D399] shrink-0">
                  {latestArtifact.artifact.fileSizeKb} KB
                </span>
              </div>

              <div className="p-3 rounded-xl bg-[#0C0E14] border border-white/8 space-y-1">
                <p className="text-xs font-semibold text-[#34D399]">
                  {latestArtifact.artifact.verificationBadge}
                </p>
                <p className="text-xs font-mono text-[#E9E9EC] break-words">
                  {latestArtifact.artifact.verificationDetail}
                </p>
              </div>

              <div className="grid grid-cols-3 gap-3 text-xs">
                <div className="p-2.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  <p className="text-[#99999E]">{t.metricTruePeak}</p>
                  <p className="font-mono tabular-nums text-white font-semibold mt-1">
                    {latestArtifact.artifact.truePeakDbtp} dBTP
                  </p>
                </div>
                <div className="p-2.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  <p className="text-[#99999E]">{t.metricIntegrated}</p>
                  <p className="font-mono tabular-nums text-white font-semibold mt-1">
                    {latestArtifact.artifact.integratedLufs} LUFS
                  </p>
                </div>
                <div className="p-2.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  <p className="text-[#99999E]">{t.metricHeadroom}</p>
                  <p className="font-mono tabular-nums text-[#34D399] font-semibold mt-1">
                    +{latestArtifact.artifact.headroomDb} dB
                  </p>
                </div>
              </div>

              <a
                href={latestArtifact.downloadUrl}
                download={latestArtifact.artifact.fileName}
                className="w-full inline-flex items-center justify-center gap-2 px-4 py-2.5 text-sm font-semibold rounded-xl bg-[#34D399] hover:bg-[#22C58B] text-[#080A10] transition-colors"
              >
                <Download className="w-4 h-4 shrink-0" />
                <span className="truncate">
                  {t.downloadFile.replace('{file}', latestArtifact.artifact.fileName)}
                </span>
              </a>
            </div>
          )}

          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-3">
            <div className="flex items-center justify-between gap-2">
              <h3 className="text-sm font-semibold text-white">
                {t.w12BatchQueueTitle} ({batchQueue.length})
              </h3>
              {batchQueue.length > 0 && (
                <button
                  type="button"
                  onClick={handleProcessBatch}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg bg-[#387AFF] hover:bg-[#598FFF] text-white whitespace-nowrap shrink-0"
                >
                  <Play className="w-3 h-3" />
                  <span>{t.w12ProcessBatchBtn}</span>
                </button>
              )}
            </div>

            {batchQueue.length === 0 ? (
              <p className="text-xs text-[#99999E] py-3">{t.w12BatchEmpty}</p>
            ) : (
              <div className="space-y-2">
                {batchQueue.map((job) => (
                  <div
                    key={job.id}
                    className="flex items-center justify-between gap-3 p-3 rounded-xl bg-[#1C1E27] border border-white/8 text-xs"
                  >
                    <div className="min-w-0">
                      <p className="font-semibold text-white truncate">{job.presetLabel}</p>
                      <p className="text-[#99999E] mt-0.5 font-mono truncate">
                        {job.status === 'done' && job.artifact
                          ? `${job.artifact.verificationBadge} · ${job.artifact.fileSizeKb} KB`
                          : `${job.extension} · Order ${job.hoaOrder} · ${job.ceilingDbtp} dBTP`}
                      </p>
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      {job.status === 'done' && job.artifact && job.downloadUrl ? (
                        <a
                          href={job.downloadUrl}
                          download={job.artifact.fileName}
                          aria-label={`Download ${job.artifact.fileName}`}
                          className="inline-flex items-center gap-1 px-2 py-1 rounded-md bg-[#34D399]/18 hover:bg-[#34D399]/30 text-[#34D399] font-mono font-semibold uppercase"
                        >
                          <Download className="w-3 h-3" />
                          <span>{job.extension.replace('.', '')}</span>
                        </a>
                      ) : (
                        <span className="font-mono text-[#89B0FF]">{t.statusQueued}</span>
                      )}
                      <button
                        type="button"
                        aria-label={`Remove ${job.presetLabel} from batch queue`}
                        onClick={() =>
                          setBatchQueue((prev) => prev.filter((item) => item.id !== job.id))
                        }
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
    </div>
  );
};
