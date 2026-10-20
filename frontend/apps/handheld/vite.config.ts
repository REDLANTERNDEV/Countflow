import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  define: { __COUNTFLOW_STATIC_DEMO__: JSON.stringify(process.env.VITE_DEMO_MODE === 'true') },
  // Served under /handheld by Caddy so both SPAs share one origin.
  base: '/handheld/',
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      // 'prompt', not 'autoUpdate' — and nothing prompts anybody. From Phase 8
      // the app holds the operator's unsent work, so the moment a new build
      // takes the page over is a decision rather than a default. `src/
      // serviceWorker.ts` applies it when the device is quiet; that file has
      // the argument, including why a reload mid-queue would have been *safe*
      // and still not something to do to somebody typing a count.
      registerType: 'prompt',
      injectRegister: null,
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
        // worse than no figures. Writes made while offline go to the IndexedDB
        // command queue in `src/lib/outbox/` — never to a cached response that
        // would let the device pretend the warehouse had answered.
        navigateFallback: '/handheld/index.html',
        runtimeCaching: [
          {
            urlPattern: ({ url }) => url.pathname.startsWith('/api/v1/'),
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
    sourcemap: process.env.VITE_DEMO_MODE !== 'true',
  },
});
