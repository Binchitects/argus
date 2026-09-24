/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// `npm run dev` serves the UI on :5173 and forwards API calls to the backend on :5080.
const api = process.env.API_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  build: { minify: process.env.NO_MINIFY ? false : 'oxc' },
  server: {
    proxy: { '/api': api, '/healthz': api, '/readyz': api },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
  },
})
