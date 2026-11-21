import React, { useState } from 'react';
import type { SpeakerChannelSpec, SpeakerLayout } from '../types/cavern';

interface LayoutDiagramProps {
  layout: SpeakerLayout;
  channelsLabel?: string;
}

export const LayoutDiagram: React.FC<LayoutDiagramProps> = ({ layout, channelsLabel = 'channels' }) => {
  const [selectedSpeakerId, setSelectedSpeakerId] = useState<string | null>(
    layout.speakers[0]?.id ?? null,
  );

  const activeSpeaker: SpeakerChannelSpec | null =
    layout.speakers.find((s) => s.id === selectedSpeakerId) ??
    layout.speakers[0] ??
    null;

  return (
    <div className="bg-[#0C0E14] border border-white/10 rounded-xl p-4 space-y-3">
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs font-semibold text-white">{layout.name}</span>
        <span className="text-xs font-mono tabular-nums text-[#89B0FF]">
          {layout.channelCount} {channelsLabel}
        </span>
      </div>

      <svg
        viewBox="0 0 320 260"
        className="w-full h-[220px] block"
        role="img"
        aria-label={`Speaker wiring diagram for ${layout.name}`}
      >
        {/* Sweet-spot reference rings */}
        <circle cx="160" cy="134" r="96" fill="none" stroke="rgba(255,255,255,0.08)" strokeWidth="1" />
        <circle cx="160" cy="134" r="64" fill="none" stroke="rgba(89,143,255,0.18)" strokeDasharray="4 4" strokeWidth="1" />
        <line x1="160" y1="30" x2="160" y2="238" stroke="rgba(255,255,255,0.05)" />
        <line x1="56" y1="134" x2="264" y2="134" stroke="rgba(255,255,255,0.05)" />

        {/* Screen bar at top */}
        <rect x="98" y="16" width="124" height="6" rx="3" fill="rgba(56,122,255,0.5)" />
        <text x="160" y="12" textAnchor="middle" fill="#99999E" fontSize="9" fontFamily="JetBrains Mono, monospace">
          FRONT / SCREEN
        </text>

        {/* Listener head at center */}
        <circle cx="160" cy="134" r="11" fill="#1E273D" stroke="#FCFCFF" strokeWidth="1.6" />
        <polygon points="160,118 155,125 165,125" fill="#598FFF" />

        {/* Speaker nodes */}
        {layout.speakers.map((sp) => {
          const rad = (sp.azimuthDeg * Math.PI) / 180;
          const r = sp.kind === 'height' ? 58 : 98;
          const cx = sp.kind === 'lfe' ? 160 : 160 + Math.sin(rad) * r;
          const cy = sp.kind === 'lfe' ? 76 : 134 - Math.cos(rad) * r;
          const isSelected = activeSpeaker?.id === sp.id;
          const fill =
            sp.kind === 'lfe' ? '#A855F7' : sp.kind === 'height' ? '#38BDF8' : '#598FFF';

          return (
            <g
              key={sp.id}
              className="cursor-pointer"
              onClick={() => setSelectedSpeakerId(sp.id)}
            >
              {isSelected && (
                <circle cx={cx} cy={cy} r="15" fill="none" stroke="#FACC15" strokeWidth="1.8" />
              )}
              <circle cx={cx} cy={cy} r="11" fill={fill} opacity={isSelected ? 1 : 0.85} />
              <text
                x={cx}
                y={cy + 3.5}
                textAnchor="middle"
                fill="#080A10"
                fontSize="8.5"
                fontWeight="700"
                fontFamily="JetBrains Mono, monospace"
              >
                {sp.label}
              </text>
            </g>
          );
        })}
      </svg>

      {activeSpeaker && (
        <div className="pt-2 border-t border-white/8 text-xs space-y-1">
          <div className="flex items-center justify-between">
            <span className="font-semibold text-white">
              {activeSpeaker.label} — {activeSpeaker.name}
            </span>
            <span className="font-mono tabular-nums text-[#99999E]">
              Az {activeSpeaker.azimuthDeg}° · El {activeSpeaker.elevationDeg}°
            </span>
          </div>
          <p className="text-[#B7B7BB]">
            {activeSpeaker.matrixNote
              ? activeSpeaker.matrixNote
              : `Connect ${activeSpeaker.name} to output ${activeSpeaker.outputPort}.`}
          </p>
        </div>
      )}
    </div>
  );
};
