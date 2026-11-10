import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

// The service's name people see (the repository's branding.env, copied to /branding.env in its image; CODE_INTEL_NAME in
// the environment wins): import.meta.env.VITE_CODE_INTEL_NAME (src/brand.ts) and %VITE_CODE_INTEL_NAME% in index.html.
const brandFile = fileURLToPath(new URL("../../branding.env", import.meta.url));
const brandText = existsSync(brandFile) ? readFileSync(brandFile, "utf8") : "";
process.env.VITE_CODE_INTEL_NAME = process.env.CODE_INTEL_NAME || /^CODE_INTEL_NAME=(.*)$/m.exec(brandText)?.[1]?.trim() || "Farzan Graph";
import react from "@vitejs/plugin-react";

// In development the backend runs separately (dotnet run ... serve); everything
// that is not the app goes to it. In production the backend serves dist/ itself.
const backend = process.env.CODE_INTEL_BACKEND ?? "http://127.0.0.1:7700";

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
