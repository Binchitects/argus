import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// Code Arena's web interface (code-arena web, /code-arena.html): the IDE
// (Monaco, xterm.js) and the chat's components and styles around Code Arena's
// own API, built into src/CodeArena/web, which the program embeds whole. Monaco's
// workers are files of the page's own (?worker imports): nothing is fetched from
// anywhere else. Built on its own, so the app's build (npm run build) is
// unchanged. tools/publish-code-arena.sh runs it,
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
    // Monaco's TypeScript worker alone is about 7 MB; it loads from 127.0.0.1, with the first .ts or .js file opened.
    chunkSizeWarningLimit: 8000,
    rolldownOptions: { input: 'code-arena.html' },
  },
})
