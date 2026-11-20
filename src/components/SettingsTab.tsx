import React from 'react';
import { Globe, Headphones, RotateCcw, SlidersHorizontal, Upload } from 'lucide-react';
import { HRIR_PROFILES } from '../data/catalog';
import type { StringKey } from '../i18n/strings';
import type { CavernSettings, Locale } from '../types/cavern';
import { parseQuickEqText } from '../utils/spatialMath';

interface SettingsTabProps {
  t: Record<StringKey, string>;
  settings: CavernSettings;
  onUpdateSettings: (patch: Partial<CavernSettings>) => void;
}

const SAMPLE_QUICKEQ = `Preamp: -3.5 dB
Filter 1: ON PK Fc 52 Hz Gain -4.8 dB Q 3.2
Filter 2: ON PK Fc 118 Hz Gain -3.1 dB Q 2.4
Filter 3: ON LSC Fc 90 Hz Gain 2.0 dB Q 0.71
Filter 4: ON PK Fc 3200 Hz Gain -1.6 dB Q 1.8`;

export const SettingsTab: React.FC<SettingsTabProps> = ({
  t,
  settings,
  onUpdateSettings,
}) => {
  const handleQuickEqFile = async (file: File) => {
    const text = await file.text();
    const parsed = parseQuickEqText(text);
    onUpdateSettings({
      quickEqFilters: parsed,
      quickEqFileName: file.name,
    });
  };

  return (
    <section aria-label={t.tabSettings} className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="text-xl font-bold text-white">{t.settingsTitle}</h2>
          <p className="text-sm text-[#99999E] mt-0.5">
            12 laboratory-measured 48 kHz HRIR/BRIR profiles, Cavernize 3D upmixing, and QuickEQ room correction
          </p>
        </div>

        <div className="flex items-center gap-2">
          <Globe className="w-4 h-4 text-[#89B0FF]" />
          <label htmlFor="locale-select" className="text-xs text-[#99999E]">
            {t.languageSection}:
          </label>
          <select
            id="locale-select"
            value={settings.locale}
            onChange={(e) => onUpdateSettings({ locale: e.target.value as Locale })}
            className="px-3 py-1.5 text-xs rounded-lg bg-[#15161C] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
          >
            <option value="en-US">English (en-US)</option>
            <option value="pt-BR">Português (pt-BR)</option>
          </select>
        </div>
      </div>

      {/* Official Binaural HRIR / BRIR Profiles */}
      <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <Headphones className="w-4 h-4 text-[#598FFF]" />
            <h3 className="text-sm font-semibold text-white">{t.hrirSection}</h3>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <label className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-lg bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 cursor-pointer whitespace-nowrap shrink-0">
              <Upload className="w-3.5 h-3.5 text-[#6E9CFF]" />
              <span>{t.importHrir}</span>
              <input
                type="file"
                accept=".sofa,.wav,.chrir,.chrirz"
                className="sr-only"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  if (file) {
                    onUpdateSettings({
                      customHrirName: file.name,
                      headphoneVirtualizer: true,
                    });
                  }
                  e.target.value = '';
                }}
              />
            </label>
            <button
              type="button"
              onClick={() =>
                onUpdateSettings({
                  hrirProfileId: 'sadie_d1_dfc',
                  customHrirName: null,
                  headphoneVirtualizer: true,
                })
              }
              className="px-3 py-1.5 text-xs font-medium rounded-lg bg-[#1E1F27] hover:bg-[#2A2C39] text-[#B7B7BB] border border-white/10 whitespace-nowrap shrink-0"
            >
              {t.resetHrir}
            </button>
          </div>
        </div>

        {settings.customHrirName && (
          <div className="px-3.5 py-2.5 rounded-xl bg-[#387AFF]/15 border border-[#598FFF]/40 text-xs text-[#C2D8FF]">
            Custom imported HRIR active: <span className="font-mono font-semibold">{settings.customHrirName}</span>
          </div>
        )}

        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-2.5">
          {HRIR_PROFILES.map((profile) => {
            const active = profile.id === settings.hrirProfileId && !settings.customHrirName;
            return (
              <button
                key={profile.id}
                type="button"
                onClick={() =>
                  onUpdateSettings({
                    hrirProfileId: profile.id,
                    customHrirName: null,
                    headphoneVirtualizer: profile.id !== 'direct_2_0',
                  })
                }
                className={`p-3.5 rounded-xl border text-left transition-colors ${
                  active
                    ? 'bg-[#387AFF]/18 border-[#598FFF] text-white'
                    : 'bg-[#1C1E27] border-white/8 text-[#B7B7BB] hover:text-white'
                }`}
              >
                <p className="text-xs font-semibold text-white">{profile.name}</p>
                <p className="text-[11px] text-[#99999E] mt-1 font-mono tabular-nums">
                  {profile.manikin} · {profile.distance} · {profile.reverbRt60}
                </p>
              </button>
            );
          })}
        </div>
      </div>

      {/* Bottom two columns: Upmixing & Room Calibration */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Upmixing Setup */}
        <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <SlidersHorizontal className="w-4 h-4 text-[#8E96FA]" />
              <h3 className="text-sm font-semibold text-white">{t.upmixSection}</h3>
            </div>
            <button
              type="button"
              onClick={() =>
                onUpdateSettings({
                  matrixUpmix: true,
                  cavernize3D: true,
                  upmixEffectPct: 75,
                  upmixSmoothPct: 80,
                })
              }
              className="inline-flex items-center gap-1 text-xs text-[#89B0FF] hover:text-white"
            >
              <RotateCcw className="w-3 h-3" />
              <span>{t.resetUpmix}</span>
            </button>
          </div>

          <label className="flex items-start justify-between gap-4 p-3 rounded-xl bg-[#1C1E27] border border-white/8 cursor-pointer">
            <div>
              <p className="text-sm font-medium text-white">{t.matrix71}</p>
              <p className="text-xs text-[#99999E] mt-0.5">{t.matrixUpmixDesc}</p>
            </div>
            <input
              type="checkbox"
              checked={settings.matrixUpmix}
              onChange={(e) => onUpdateSettings({ matrixUpmix: e.target.checked })}
              className="mt-1 rounded accent-[#387AFF]"
            />
          </label>

          <label className="flex items-start justify-between gap-4 p-3 rounded-xl bg-[#1C1E27] border border-white/8 cursor-pointer">
            <div>
              <p className="text-sm font-medium text-white">{t.cavernize3D}</p>
              <p className="text-xs text-[#99999E] mt-0.5">{t.cavernize3DDesc}</p>
            </div>
            <input
              type="checkbox"
              checked={settings.cavernize3D}
              onChange={(e) => onUpdateSettings({ cavernize3D: e.target.checked })}
              className="mt-1 rounded accent-[#387AFF]"
            />
          </label>

          <div className="space-y-3 pt-1">
            <div>
              <div className="flex justify-between text-xs text-[#99999E] mb-1">
                <span>{t.upmixEffect}</span>
                <span className="font-mono tabular-nums text-white">{settings.upmixEffectPct}%</span>
              </div>
              <input
                type="range"
                aria-label={t.upmixEffect}
                min={0}
                max={100}
                value={settings.upmixEffectPct}
                onChange={(e) => onUpdateSettings({ upmixEffectPct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            <div>
              <div className="flex justify-between text-xs text-[#99999E] mb-1">
                <span>{t.upmixSmooth}</span>
                <span className="font-mono tabular-nums text-white">{settings.upmixSmoothPct}%</span>
              </div>
              <input
                type="range"
                aria-label={t.upmixSmooth}
                min={0}
                max={100}
                value={settings.upmixSmoothPct}
                onChange={(e) => onUpdateSettings({ upmixSmoothPct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>
          </div>
        </div>

        {/* Calibration, QuickEQ & Dynamics */}
        <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 space-y-4">
          <h3 className="text-sm font-semibold text-white">{t.calibrationSection}</h3>

          <div className="p-3.5 rounded-xl bg-[#1C1E27] border border-white/8 space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="text-xs font-medium text-white">
                QuickEQ / Equalizer APO Filters ({settings.quickEqFilters.length} active)
              </span>
              <div className="flex items-center gap-2">
                <label className="px-2.5 py-1 text-xs rounded-md bg-[#387AFF]/20 hover:bg-[#387AFF]/35 text-[#89B0FF] cursor-pointer">
                  Load .txt
                  <input
                    type="file"
                    accept=".txt"
                    className="sr-only"
                    onChange={(e) => {
                      const f = e.target.files?.[0];
                      if (f) void handleQuickEqFile(f);
                      e.target.value = '';
                    }}
                  />
                </label>
                <button
                  type="button"
                  onClick={() =>
                    onUpdateSettings({
                      quickEqFilters: parseQuickEqText(SAMPLE_QUICKEQ),
                      quickEqFileName: 'studio-reference-quickeq.txt',
                    })
                  }
                  className="px-2.5 py-1 text-xs rounded-md bg-white/8 hover:bg-white/14 text-[#E9E9EC]"
                >
                  Load Reference Curve
                </button>
                {settings.quickEqFilters.length > 0 && (
                  <button
                    type="button"
                    onClick={() =>
                      onUpdateSettings({ quickEqFilters: [], quickEqFileName: null })
                    }
                    className="px-2.5 py-1 text-xs rounded-md bg-white/8 hover:bg-[#F56962]/20 text-[#F56962]"
                  >
                    {t.clearQuickEq}
                  </button>
                )}
              </div>
            </div>

            {settings.quickEqFilters.length > 0 ? (
              <div className="space-y-1 font-mono text-[11px] tabular-nums text-[#B7B7BB]">
                {settings.quickEqFilters.map((f, idx) => (
                  <div key={idx} className="flex justify-between">
                    <span>
                      #{idx + 1} {f.type} @ {f.freqHz} Hz
                    </span>
                    <span>
                      {f.gainDb >= 0 ? `+${f.gainDb}` : f.gainDb} dB · Q {f.q}
                    </span>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-xs text-[#99999E]">
                No room calibration EQ loaded. Import a QuickEQ `.txt` file or load the reference curve.
              </p>
            )}
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <div className="flex justify-between text-xs text-[#99999E] mb-1">
                <span>Render Gain</span>
                <span className="font-mono tabular-nums text-white">
                  {settings.renderGainDb >= 0 ? `+${settings.renderGainDb}` : settings.renderGainDb} dB
                </span>
              </div>
              <input
                type="range"
                aria-label="Render Gain dB"
                min={-12}
                max={6}
                step={0.5}
                value={settings.renderGainDb}
                onChange={(e) => onUpdateSettings({ renderGainDb: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            <div>
              <div className="flex justify-between text-xs text-[#99999E] mb-1">
                <span>Height Squeeze</span>
                <span className="font-mono tabular-nums text-white">{settings.heightSqueezePct}%</span>
              </div>
              <input
                type="range"
                aria-label="Height Squeeze percent"
                min={0}
                max={100}
                step={5}
                value={settings.heightSqueezePct}
                onChange={(e) => onUpdateSettings({ heightSqueezePct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>
          </div>

          <div className="flex flex-wrap gap-5 pt-1 text-xs text-[#E9E9EC]">
            <label className="inline-flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={settings.dynamicNormalizer}
                onChange={(e) => onUpdateSettings({ dynamicNormalizer: e.target.checked })}
                className="rounded accent-[#387AFF]"
              />
              <span>Dynamic Normalizer / Soft-Knee Limiter</span>
            </label>
            <label className="inline-flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={settings.speakerHeightVirt}
                onChange={(e) => onUpdateSettings({ speakerHeightVirt: e.target.checked })}
                className="rounded accent-[#387AFF]"
              />
              <span>Speaker Height Virtualization</span>
            </label>
          </div>
        </div>
      </div>
    </section>
  );
};
