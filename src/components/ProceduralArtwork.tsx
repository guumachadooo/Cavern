import React from 'react';
import type { SpatialTrack } from '../types/cavern';

interface ProceduralArtworkProps {
  track: SpatialTrack;
  isPlaying: boolean;
  size?: 'lg' | 'sm';
}

/**
 * Procedural acoustic spatial artwork matching CavernizeAndroid's generated cover aesthetic:
 * concentric acoustic wavefronts, phase rings, and subtle specular horizon lighting.
 */
export const ProceduralArtwork: React.FC<ProceduralArtworkProps> = ({
  track,
  isPlaying,
  size = 'lg',
}) => {
  const h1 = track.accentHue;
  const h2 = track.secondaryHue;
  const ringCount = size === 'lg' ? 6 : 3;

  return (
    <div
      className={`relative overflow-hidden select-none border border-white/10 ${
        size === 'lg' ? 'w-full aspect-square rounded-2xl' : 'w-11 h-11 rounded-lg shrink-0'
      }`}
      style={{
        background: `radial-gradient(circle at 28% 24%, hsla(${h1}, 76%, 56%, 0.42), hsla(${h2}, 52%, 22%, 0.32) 48%, #0d1018 100%)`,
      }}
      aria-hidden="true"
    >
      <svg
        viewBox="0 0 320 320"
        className="w-full h-full block"
        preserveAspectRatio="xMidYMid slice"
      >
        <defs>
          <radialGradient id={`glow-${track.id}-${size}`} cx="50%" cy="48%" r="52%">
            <stop offset="0%" stopColor={`hsla(${h1}, 92%, 68%, 0.34)`} />
            <stop offset="55%" stopColor={`hsla(${h2}, 70%, 48%, 0.14)`} />
            <stop offset="100%" stopColor="rgba(8,10,16,0)" />
          </radialGradient>
          <linearGradient id={`wave-${track.id}-${size}`} x1="0%" y1="0%" x2="100%" y2="100%">
            <stop offset="0%" stopColor={`hsla(${h1}, 92%, 72%, 0.85)`} />
            <stop offset="100%" stopColor={`hsla(${h2}, 86%, 68%, 0.25)`} />
          </linearGradient>
        </defs>

        <rect width="320" height="320" fill={`url(#glow-${track.id}-${size})`} />

        {/* Concentric acoustic pressure rings */}
        {Array.from({ length: ringCount }, (_, i) => {
          const r = 34 + i * (size === 'lg' ? 23 : 42);
          const opacity = Math.max(0.1, 0.46 - i * 0.065);
          return (
            <circle
              key={i}
              cx="160"
              cy="160"
              r={r}
              fill="none"
              stroke={`hsla(${h1}, 84%, 74%, ${opacity})`}
              strokeWidth={i === 1 ? 1.8 : 1}
              strokeDasharray={i % 2 === 1 ? '6 6' : undefined}
            />
          );
        })}

        {/* Spatial elevation meridian arcs */}
        <ellipse
          cx="160"
          cy="160"
          rx="116"
          ry="44"
          fill="none"
          stroke={`url(#wave-${track.id}-${size})`}
          strokeWidth="1.5"
          transform={`rotate(${(track.waveformSeed * 7) % 45 - 22} 160 160)`}
        />
        <ellipse
          cx="160"
          cy="160"
          rx="46"
          ry="116"
          fill="none"
          stroke={`hsla(${h2}, 85%, 74%, 0.35)`}
          strokeWidth="1.2"
          transform={`rotate(${(track.waveformSeed * 11) % 35 - 17} 160 160)`}
        />

        {/* Orbiting spatial object nodes */}
        <circle cx="88" cy="112" r={size === 'lg' ? 6 : 10} fill="#F56962" opacity="0.92" />
        <circle cx="234" cy="138" r={size === 'lg' ? 5 : 9} fill="#598FFF" opacity="0.9" />
        <circle cx="176" cy="226" r={size === 'lg' ? 4.5 : 8} fill="#38BDF8" opacity="0.85" />

        {/* Center sweet-spot emblem */}
        <circle
          cx="160"
          cy="160"
          r={size === 'lg' ? (isPlaying ? 16 : 13) : 22}
          fill={`hsla(${h1}, 90%, 64%, 0.25)`}
          stroke="#FCFCFF"
          strokeWidth="2"
        />
        <circle cx="160" cy="160" r={size === 'lg' ? 5 : 8} fill="#FCFCFF" />
      </svg>

      {size === 'lg' && (
        <div className="absolute inset-x-0 bottom-0 h-20 bg-gradient-to-t from-[#080A10]/85 via-[#080A10]/30 to-transparent pointer-events-none" />
      )}
    </div>
  );
};
