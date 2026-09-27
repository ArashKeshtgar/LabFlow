import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The API runs on :5180 (src/LabFlow.Api). Proxying keeps the browser on one origin,
// so the API needs no CORS policy.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    strictPort: true,
    proxy: { '/api': 'http://localhost:5180' },
  },
})
