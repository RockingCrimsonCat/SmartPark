import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// У режимі розробки запити /api і /hubs проксіюються на сервер ASP.NET Core.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5000',
      '/hubs': { target: 'http://localhost:5000', ws: true },
    },
  },
});
