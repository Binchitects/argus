import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests run against a running app:
 *   E2E_BASE_URL=https://llm.localhost E2E_USER=admin E2E_PASSWORD=... npm run e2e   (deployed stack, via Authelia)
 *   E2E_BASE_URL=http://localhost:8080 npm run e2e                                  (bare app container, as in CI)
 * E2E_CHANNEL=chrome uses the installed Google Chrome instead of Playwright's own
 * browser (for machines where `npx playwright install` cannot download).
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
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
