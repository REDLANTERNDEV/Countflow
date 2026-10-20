import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  define: { __COUNTFLOW_STATIC_DEMO__: JSON.stringify(process.env.VITE_DEMO_MODE === 'true') },
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    // Same-origin in dev as in production behind Caddy, so no code path differs
    // between the two and CORS stays out of the picture.
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
