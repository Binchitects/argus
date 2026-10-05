import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// Code Arena's web interface (code-arena web, /code-arena.html): the chat's
// components and styles around Code Arena's own API, built into
// src/CodeArena/web, which the program embeds whole. Built on its own, so the
// app's build (npm run build) is unchanged. tools/publish-code-arena.sh runs it,
// then the preview runner's build into the same folder (diagrams in answers):
//   node_modules/.bin/vite build --config vite.code-arena.config.ts
//   node_modules/.bin/vite build --config vite.preview.config.ts --outDir ../CodeArena/web
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: '../CodeArena/web',
    emptyOutDir: true,
    minify: process.env.NO_MINIFY ? false : 'oxc',
    // Never inline assets as data: URLs: the page's CSP allows fonts and images from its own origin.
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 2000,
    rolldownOptions: { input: 'code-arena.html' },
  },
})
