import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The AppHost sets PORT (5174 for Contoso, 5175 for Fabrikam) and the VITE_* variables.
export default defineConfig({
  plugins: [react()],
  server: { host: 'localhost', port: Number(process.env.PORT) || 5174, strictPort: true },
});
