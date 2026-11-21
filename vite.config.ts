import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  optimizeDeps: {
    entries: ['index.html', 'src/main.tsx'],
  },
  server: {
    host: '0.0.0.0',
    port: 3000,
    allowedHosts: true,
    hmr: false,
    watch: {
      ignored: [
        '**/Cavern/**',
        '**/Cavern.Format/**',
        '**/CavernSamples/**',
        '**/Libraries/**',
        '**/.agents/**',
        '**/.github/**',
      ],
    },
  },
  preview: {
    host: '0.0.0.0',
    port: 3000,
  },
});
