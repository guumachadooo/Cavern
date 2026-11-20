import React, { useMemo, useState } from 'react';
import { Download, FileJson, Layers } from 'lucide-react';
import { renderStemWav } from '../audio/spatialEngine';
import type { StringKey } from '../i18n/strings';
import type { SpatialSourceNode, SpatialTrack } from '../types/cavern';
import { buildTrajectoryJson } from '../utils/spatialMath';

interface StemsTabProps {
  t: Record<StringKey, string>;
  activeTrack: SpatialTrack;
  sources: SpatialSourceNode[];
}

interface ExtractedStemResult {
  id: string;
  name: string;
  fileName: string;
  sizeKb: number;
  url: string;
}

export const StemsTab: React.FC<StemsTabProps> = ({ t, activeTrack, sources }) => {
  const [extractMode, setExtractMode] = useState<'mono-stems' | 'spatial-each' | '3d-combined'>('mono-stems');
  const [bitDepth, setBitDepth] = useState<'16' | '24' | '32f'>('24');
  const [exportJson, setExportJson] = useState(true);
  const [selectedIds, setSelectedIds] = useState<string[]>(() => sources.map((s) => s.id));
  const [extractedStems, setExtractedStems] = useState<ExtractedStemResult[]>([]);
  const [trajectoryPreview, setTrajectoryPreview] = useState<string | null>(null);
  const [trajectoryUrl, setTrajectoryUrl] = useState<string | null>(null);

  const selectedSources = useMemo(
    () => sources.filter((s) => selectedIds.includes(s.id)),
    [sources, selectedIds],
  );

  const toggleSource = (id: string) => {
    setSelectedIds((prev) =>
      prev.includes(id) ? prev.filter((item) => item !== id) : [...prev, id],
    );
  };

  const handleExtract = () => {
    if (selectedSources.length === 0) return;

    const results = selectedSources.map((src) => {
      const rendered = renderStemWav(src);
      return {
        id: src.id,
        name: src.name,
        fileName: rendered.fileName,
        sizeKb: rendered.sizeKb,
        url: rendered.url,
      };
    });
    setExtractedStems(results);

    if (exportJson) {
      const jsonStr = buildTrajectoryJson(activeTrack.title, selectedSources, 4, 10);
      setTrajectoryPreview(jsonStr);
      const blob = new Blob([jsonStr], { type: 'application/json' });
      setTrajectoryUrl(URL.createObjectURL(blob));
    } else {
      setTrajectoryPreview(null);
      setTrajectoryUrl(null);
    }
  };

  return (
    <section aria-label={t.tabStems} className="space-y-6">
      <div>
        <h2 className="text-xl font-bold text-white">{t.stemsTitle}</h2>
        <p className="text-sm text-[#99999E] mt-1 max-w-3xl">{t.stemsDesc}</p>
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
        {/* Left 7 cols: Extraction Options & Channel/Object Checklist */}
        <div className="xl:col-span-7 bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-5">
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            <div className="sm:col-span-2 space-y-1.5">
              <label htmlFor="stem-mode" className="text-xs font-medium text-[#99999E] block">
                {t.extractMode}
              </label>
              <select
                id="stem-mode"
                value={extractMode}
                onChange={(e) => setExtractMode(e.target.value as typeof extractMode)}
                className="w-full px-3 py-2 text-sm rounded-xl bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
              >
                <option value="mono-stems">Separated Mono WAV per Channel & Object + 3D JSON</option>
                <option value="spatial-each">Render Each Selected Item in 3D Separately (HRTF)</option>
                <option value="3d-combined">Render Selected 3D Objects Combined (1 Spatial File)</option>
              </select>
            </div>

            <div className="space-y-1.5">
              <label htmlFor="stem-bitdepth" className="text-xs font-medium text-[#99999E] block">
                {t.bitDepth}
              </label>
              <select
                id="stem-bitdepth"
                value={bitDepth}
                onChange={(e) => setBitDepth(e.target.value as typeof bitDepth)}
                className="w-full px-3 py-2 text-sm rounded-xl bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
              >
                <option value="16">16-bit PCM</option>
                <option value="24">24-bit PCM</option>
                <option value="32f">32-bit Float</option>
              </select>
            </div>
          </div>

          <label className="flex items-center gap-2.5 text-sm text-[#E9E9EC] cursor-pointer">
            <input
              type="checkbox"
              checked={exportJson}
              onChange={(e) => setExportJson(e.target.checked)}
              className="rounded accent-[#387AFF]"
            />
            <span>{t.exportTrajectoryJson}</span>
          </label>

          {/* Selection quick actions */}
          <div className="flex flex-wrap items-center justify-between gap-2 pt-2 border-t border-white/8">
            <span className="text-xs font-mono tabular-nums text-[#B7B7BB]">
              {selectedIds.length} of {sources.length} selected
            </span>
            <div className="flex flex-wrap items-center gap-1.5">
              <button
                type="button"
                onClick={() => setSelectedIds(sources.map((s) => s.id))}
                className="px-2.5 py-1 text-xs rounded-md bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10"
              >
                {t.selectAll}
              </button>
              <button
                type="button"
                onClick={() =>
                  setSelectedIds(sources.filter((s) => s.kind === 'object').map((s) => s.id))
                }
                className="px-2.5 py-1 text-xs rounded-md bg-[#1E1F27] hover:bg-[#2A2C39] text-[#F56962] border border-white/10"
              >
                {t.select3D}
              </button>
              <button
                type="button"
                onClick={() =>
                  setSelectedIds(
                    sources.filter((s) => s.kind === 'bed' || s.kind === 'lfe').map((s) => s.id),
                  )
                }
                className="px-2.5 py-1 text-xs rounded-md bg-[#1E1F27] hover:bg-[#2A2C39] text-[#89B0FF] border border-white/10"
              >
                {t.selectBeds}
              </button>
              <button
                type="button"
                onClick={() =>
                  setSelectedIds(sources.filter((s) => !selectedIds.includes(s.id)).map((s) => s.id))
                }
                className="px-2.5 py-1 text-xs rounded-md bg-[#1E1F27] hover:bg-[#2A2C39] text-[#B7B7BB] border border-white/10"
              >
                {t.invertSelection}
              </button>
            </div>
          </div>

          {/* Grid of selectable channels & 3D objects */}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
            {sources.map((src) => {
              const checked = selectedIds.includes(src.id);
              return (
                <label
                  key={src.id}
                  className={`flex items-center justify-between gap-3 px-3 py-2.5 rounded-xl border cursor-pointer transition-colors ${
                    checked
                      ? 'bg-[#387AFF]/15 border-[#598FFF]/50 text-white'
                      : 'bg-[#1C1E27] border-white/6 text-[#99999E] hover:text-white'
                  }`}
                >
                  <div className="flex items-center gap-2.5 min-w-0">
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={() => toggleSource(src.id)}
                      className="rounded accent-[#387AFF]"
                    />
                    <span className="text-xs font-medium truncate">{src.name}</span>
                  </div>
                  <span className="text-[11px] font-mono uppercase shrink-0 text-[#89B0FF]">
                    {src.kind}
                  </span>
                </label>
              );
            })}
          </div>

          <button
            type="button"
            disabled={selectedIds.length === 0}
            onClick={handleExtract}
            className="inline-flex items-center gap-2 px-5 py-2.5 text-sm font-semibold rounded-xl bg-[#387AFF] hover:bg-[#598FFF] disabled:opacity-40 text-white transition-colors"
          >
            <Layers className="w-4 h-4" />
            <span>
              {t.extractStems} ({selectedIds.length})
            </span>
          </button>
        </div>

        {/* Right 5 cols: Extracted Stems Download List & 3D Trajectory JSON Output */}
        <div className="xl:col-span-5 space-y-5">
          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-3">
            <h3 className="text-sm font-semibold text-white">Extracted Audio Stems</h3>
            {extractedStems.length === 0 ? (
              <p className="text-xs text-[#99999E] py-4">
                Select bed channels or dynamic 3D objects on the left and click Extract Selected Stems to generate downloadable mono WAV files.
              </p>
            ) : (
              <div className="space-y-2 max-h-[280px] overflow-y-auto pr-1">
                {extractedStems.map((stem) => (
                  <div
                    key={stem.id}
                    className="flex items-center justify-between gap-3 px-3 py-2 rounded-xl bg-[#1C1E27] border border-white/8 text-xs"
                  >
                    <div className="min-w-0">
                      <p className="font-medium text-white truncate">{stem.name}</p>
                      <p className="font-mono text-[#99999E] tabular-nums">
                        {stem.fileName} · {stem.sizeKb} KB
                      </p>
                    </div>
                    <a
                      href={stem.url}
                      download={stem.fileName}
                      className="inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg bg-[#387AFF]/20 hover:bg-[#387AFF]/35 text-[#89B0FF] font-medium shrink-0"
                    >
                      <Download className="w-3.5 h-3.5" />
                      <span>WAV</span>
                    </a>
                  </div>
                ))}
              </div>
            )}
          </div>

          <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-3">
            <div className="flex items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                <FileJson className="w-4 h-4 text-[#56B8A8]" />
                <h3 className="text-sm font-semibold text-white">3D Trajectory Metadata (.json)</h3>
              </div>
              {trajectoryUrl && (
                <a
                  href={trajectoryUrl}
                  download="cavern-3d-trajectories.json"
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg bg-[#56B8A8] hover:bg-[#48A696] text-[#080A10]"
                >
                  <Download className="w-3.5 h-3.5" />
                  <span>Download JSON</span>
                </a>
              )}
            </div>

            {trajectoryPreview ? (
              <pre className="p-3 rounded-xl bg-[#0C0E14] border border-white/8 text-[11px] font-mono text-[#B7B7BB] overflow-auto max-h-[260px]">
                {trajectoryPreview.slice(0, 2000)}
                {trajectoryPreview.length > 2000 ? '\n... (truncated preview — download full JSON above)' : ''}
              </pre>
            ) : (
              <p className="text-xs text-[#99999E] py-3">
                Frame-by-frame (X, Y, Z) 3D object coordinates will appear here after extraction.
              </p>
            )}
          </div>
        </div>
      </div>
    </section>
  );
};
