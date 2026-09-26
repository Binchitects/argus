import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// In development the backend runs separately (dotnet run ... serve); everything
// that is not the app goes to it. In production the backend serves dist/ itself.
const backend = process.env.ARGUS_BACKEND ?? "http://127.0.0.1:7700";

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      "/api": { target: backend, changeOrigin: false },
      "/admin": { target: backend, changeOrigin: false },
      "/mcp": { target: backend, changeOrigin: false },
    },
  },
  build: { outDir: "dist", sourcemap: false, chunkSizeWarningLimit: 800 },
});
