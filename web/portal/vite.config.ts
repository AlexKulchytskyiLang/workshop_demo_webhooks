import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The AppHost sets PORT (and the VITE_* variables). The port is fixed because Keycloak only accepts
// the registered redirect URI http://localhost:5173/*.
export default defineConfig({
  plugins: [react()],
  server: { host: 'localhost', port: Number(process.env.PORT) || 5173, strictPort: true },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
