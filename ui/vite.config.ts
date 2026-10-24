import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: '/ui/',
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:7415',
      '/health': 'http://127.0.0.1:7415',
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
});
