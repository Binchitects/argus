import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// Arena Code's web interface (arena-code web, /arena-code.html): the chat's
// components and styles around Arena Code's own API, built into
// src/ArenaCode/web, which the program embeds whole. Built on its own, so the
// app's build (npm run build) is unchanged. tools/publish-arena-code.sh runs it,
// then the preview runner's build into the same folder (diagrams in answers):
//   node_modules/.bin/vite build --config vite.arena-code.config.ts
//   node_modules/.bin/vite build --config vite.preview.config.ts --outDir ../ArenaCode/web
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: '../ArenaCode/web',
    emptyOutDir: true,
    minify: process.env.NO_MINIFY ? false : 'oxc',
    // Never inline assets as data: URLs: the page's CSP allows fonts and images from its own origin.
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 2000,
    rolldownOptions: { input: 'arena-code.html' },
  },
})
