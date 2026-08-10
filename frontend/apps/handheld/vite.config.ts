import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  // Served under /handheld by Caddy so both SPAs share one origin.
  base: '/handheld/',
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.svg'],
      manifest: {
        name: 'Countflow Handheld',
        short_name: 'Countflow',
        description: 'Directed warehouse work',
        start_url: '/handheld/',
        scope: '/handheld/',
        display: 'standalone',
        // Scanners are held in portrait and the UI is a vertical task list.
        orientation: 'portrait',
        background_color: '#241C1A',
        theme_color: '#241C1A',
        icons: [
          { src: 'icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icon-512.png', sizes: '512x512', type: 'image/png' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        // The app shell is cached so a device in a dead zone still starts.
        // API traffic is deliberately never cached: stale stock figures are
        // worse than no figures. Phase 3 adds the IndexedDB command queue for
        // writes made while offline.
        navigateFallback: '/handheld/index.html',
        runtimeCaching: [
          {
            urlPattern: ({ url }) => url.pathname.startsWith('/api/'),
            handler: 'NetworkOnly',
          },
        ],
      },
      devOptions: {
        enabled: true,
        type: 'module',
      },
    }),
  ],
  server: {
    port: 5174,
    proxy: {
      '/api': {
        target: process.env.COUNTFLOW_API_URL ?? 'http://localhost:8080',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
