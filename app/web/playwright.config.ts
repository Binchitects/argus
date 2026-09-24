import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests run against a running app, signed in as its admin:
 *   E2E_BASE_URL=https://llm.localhost E2E_PASSWORD=... npm run e2e         (the deployed stack)
 *   E2E_BASE_URL=https://llm.localhost:8443 E2E_PASSWORD=... npm run e2e    (a bare app container, as in CI)
 * E2E_CHANNEL=chrome uses the installed Google Chrome instead of Playwright's own
 * browser (for machines where `npx playwright install` cannot download).
 * Runs against the live model (E2E_CHAT=1) retry once, as CI does: an installed Chrome
 * reloads its certificate store now and then, failing whatever request is in flight
 * (net::ERR_FAILED, measured). A retry that passes is still reported as "flaky".
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI || process.env.E2E_CHAT ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  globalSetup: './e2e/global-setup.ts',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'https://llm.localhost',
    ignoreHTTPSErrors: true,
    storageState: 'e2e/.auth/state.json',
    trace: 'retain-on-failure',
    channel: process.env.E2E_CHANNEL || undefined,
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
})
