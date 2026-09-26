import { defineConfig, devices } from "@playwright/test";

// One backend for the whole run, started by global-setup with fake GitLab,
// LiteLLM and embeddings around it; the specs share its state, so they run in
// order in one worker.
const port = Number(process.env.E2E_PORT ?? 7811);
process.env.E2E_PORT = String(port);

export default defineConfig({
  testDir: "./e2e",
  globalSetup: "./e2e/global-setup.ts",
  fullyParallel: false,
  workers: 1,
  timeout: 120_000,
  expect: { timeout: 20_000 },
  reporter: [["list"]],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    viewport: { width: 1400, height: 900 },
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1400, height: 900 } } }],
});
