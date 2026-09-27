import { defineConfig } from 'vite'

// The chat's preview runner (/preview.html), built on its own: it needs every
// icon and React in one place, which shared chunks would push into the app.
// Output lands beside the app's, under preview/, in the same dist folder.
export default defineConfig({
  build: {
    outDir: 'dist',
    emptyOutDir: false,
    assetsDir: 'preview/assets',
    minify: process.env.NO_MINIFY ? false : 'oxc',
    assetsInlineLimit: 0,
    // Mermaid is one 5.5 MB file, loaded only for a diagram.
    chunkSizeWarningLimit: 6000,
    rolldownOptions: {
      input: 'preview.html',
      // lucide-react marks its files "use client", which means nothing here.
      onLog: (level, log, handler) => (log.code === 'MODULE_LEVEL_DIRECTIVE' ? undefined : handler(level, log)),
    },
  },
})
