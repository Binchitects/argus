/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// `npm run dev` serves the UI on :5173 and forwards the API to a running stack
// (API_URL, default https://llm.localhost), the way Traefik does in production.
const api = process.env.API_URL ?? 'https://llm.localhost'
const proxy = { target: api, changeOrigin: true, secure: false }

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    minify: process.env.NO_MINIFY ? false : 'oxc',
    // Never inline assets as data: URLs: the CSP allows fonts and images from this origin.
    assetsInlineLimit: 0,
    rolldownOptions: {
      output: {
        // Libraries change less often than the app: separate files stay cached across releases.
        codeSplitting: {
          groups: [
            { name: 'react', test: /node_modules[\\/](react|react-dom|scheduler|react-router)[\\/]/, priority: 3 },
            { name: 'forms', test: /node_modules[\\/](zod|react-hook-form|@hookform)[\\/]/, priority: 2 },
            { name: 'ui', test: /node_modules[\\/](@radix-ui|radix-ui|@floating-ui|react-remove-scroll|react-style-singleton|use-sidecar|aria-hidden|cmdk|sonner|lucide-react|tailwind-merge|clsx|class-variance-authority)[\\/]/, priority: 1 },
          ],
        },
      },
    },
  },
  server: {
    proxy: { '/api': proxy, '/connect': proxy, '/.well-known': proxy },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
    css: false,
  },
})
