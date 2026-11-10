/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { defineConfig, searchForWorkspaceRoot } from 'vite'

// `npm run dev` serves the UI on :5173 and forwards the API to a running stack
// (API_URL, default https://llm.localhost), the way Traefik does in production.
const api = process.env.API_URL ?? 'https://llm.localhost'
const proxy = { target: api, changeOrigin: true, secure: false }

// The manual (src/help/manual.ts) builds the repository's docs/ into the page. In the web image
// they are copied to /docs, which ../../docs from /web names too (src/web/Dockerfile).
const docs = fileURLToPath(new URL('../../docs', import.meta.url))

// The names people see (the repository's branding.env, copied to /branding.env in the web image; PRODUCT_NAME and
// CODE_INTEL_NAME in the environment win): import.meta.env.VITE_PRODUCT_NAME and VITE_CODE_INTEL_NAME (src/lib/brand.ts),
// and %VITE_PRODUCT_NAME% in index.html.
const brandFile = fileURLToPath(new URL('../../branding.env', import.meta.url))
const brandText = existsSync(brandFile) ? readFileSync(brandFile, 'utf8') : ''
const fromFile = (key: string) => new RegExp(`^${key}=(.*)$`, 'm').exec(brandText)?.[1]?.trim()
process.env.VITE_PRODUCT_NAME = process.env.PRODUCT_NAME || fromFile('PRODUCT_NAME') || 'Farzan Arena'
process.env.VITE_CODE_INTEL_NAME = process.env.CODE_INTEL_NAME || fromFile('CODE_INTEL_NAME') || 'Farzan Graph'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)), '@docs': docs } },
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
            { name: 'charts', test: /node_modules[\\/](echarts|zrender|marked|dompurify)[\\/]/, priority: 2 },
            { name: 'forms', test: /node_modules[\\/](zod|react-hook-form|@hookform)[\\/]/, priority: 2 },
            { name: 'ui', test: /node_modules[\\/](@radix-ui|radix-ui|@floating-ui|react-remove-scroll|react-style-singleton|use-sidecar|aria-hidden|cmdk|sonner|lucide-react|tailwind-merge|clsx|class-variance-authority)[\\/]/, priority: 1 },
          ],
        },
      },
    },
  },
  server: {
    proxy: { '/api': proxy, '/connect': proxy, '/.well-known': proxy, '^/mcp(/code)?$': proxy, '/scim': proxy },
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), docs] },
  },
  test: {
    environment: 'jsdom',
    // The first test of a page pays for compiling it (the chat's markdown,
    // highlighting and maths); on a busy CI runner that alone can pass 5 s.
    testTimeout: 15_000,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
    css: false,
  },
})
